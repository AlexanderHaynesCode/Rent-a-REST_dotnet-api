using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RentARestaurant.Api.Contracts;
using RentARestaurant.Api.Data;
using RentARestaurant.Api.Domain.Entities;
using RentARestaurant.Api.Infrastructure.Email;
using RentARestaurant.Api.Infrastructure.Storage;
using RentARestaurant.Api.Infrastructure.Tenancy;
using RentARestaurant.Api.Services;

namespace RentARestaurant.Api.Controllers;

/// <summary>
/// Internal API consumed only by the agent-executor-service. Requires the shared
/// X-Agent-Api-Key secret plus a resolved tenant (via X-Tenant-Slug header, same as the admin app).
/// </summary>
[ApiController]
[Route("api/agent/restaurant")]
[RequireInternalAgentKey]
[RequireTenantContext]
public class AgentRestaurantController(
    AppDbContext dbContext,
    ITenantContext tenantContext,
    IRestaurantAdminService restaurantAdminService,
    IEmailService emailService,
    IR2StorageService r2StorageService) : ControllerBase
{
    private const string DoneForYouPlan = "Done-For-You";
    private const long MaxImageBytes = 10 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [HttpGet("snapshot")]
    public async Task<ActionResult<AgentSnapshotResponse>> GetSnapshot(CancellationToken cancellationToken)
    {
        var tenantId = tenantContext.TenantId!.Value;
        var tenant = await dbContext.Tenants.AsNoTracking().SingleAsync(x => x.Id == tenantId, cancellationToken);
        var snapshot = await restaurantAdminService.GetSnapshotAsync(tenantId, cancellationToken);

        return Ok(new AgentSnapshotResponse(snapshot, tenant.SubscriptionPlan == DoneForYouPlan));
    }

    [HttpPost("apply")]
    public async Task<ActionResult<AgentApplyResponse>> Apply(
        [FromBody] ApplyAgentChangeSetRequest request,
        CancellationToken cancellationToken)
    {
        var tenantId = tenantContext.TenantId!.Value;

        var tenant = await dbContext.Tenants.SingleAsync(x => x.Id == tenantId, cancellationToken);
        if (tenant.SubscriptionPlan != DoneForYouPlan)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                Error = "Tenant is not eligible for agent-applied updates (requires Done-For-You plan)."
            });
        }

        var submission = await dbContext.AgentSubmissions
            .SingleOrDefaultAsync(x => x.Id == request.SubmissionId && x.TenantId == tenantId, cancellationToken);
        if (submission is null)
        {
            return NotFound(new { Error = "Submission not found for this tenant." });
        }

        var preChangeSnapshot = await restaurantAdminService.GetSnapshotAsync(tenantId, cancellationToken);
        var appliedSummary = new List<string>();

        var (activeDateHours, rejectedSummary, dateHoursError) = ValidateDateSpecificHours(request.ChangeSet.DateSpecificHoursChanges);
        if (dateHoursError is not null)
        {
            return BadRequest(new { Error = dateHoursError });
        }

        if (request.ChangeSet.BrandingChange is { } branding)
        {
            await ApplyBrandingChangeAsync(tenantId, branding, cancellationToken, appliedSummary);
        }

        if (request.ChangeSet.HoursChanges is { Count: > 0 } hours)
        {
            var hourRequests = hours
                .Select(h => new UpsertBusinessHourRequest(h.DayOfWeek, h.OpenTime, h.CloseTime, h.IsClosed))
                .ToList();
            await restaurantAdminService.UpsertHoursAsync(tenantId, hourRequests, cancellationToken);
            appliedSummary.Add($"Updated business hours ({hours.Count} day(s)).");
        }

        if (activeDateHours is { Count: > 0 } dateHours)
        {
            var upserts = dateHours.Where(h => IsAction(h.Action, "upsert")).ToList();
            if (upserts.Count > 0)
            {
                await restaurantAdminService.UpsertDateSpecificHoursAsync(
                    tenantId,
                    upserts.Select(h => new UpsertDateSpecificHourRequest(
                        h.Date, h.OpenTime ?? default, h.CloseTime ?? default, h.IsClosed ?? false)).ToList(),
                    cancellationToken);

                foreach (var h in upserts)
                {
                    appliedSummary.Add(h.IsClosed == true
                        ? $"Set {h.Date:yyyy-MM-dd} as closed."
                        : $"Set hours for {h.Date:yyyy-MM-dd} to {h.OpenTime:HH:mm}-{h.CloseTime:HH:mm}.");
                }
            }

            foreach (var h in dateHours.Where(h => IsAction(h.Action, "delete")))
            {
                var removed = await restaurantAdminService.DeleteDateSpecificHourAsync(tenantId, h.Date, cancellationToken);
                appliedSummary.Add(removed
                    ? $"Removed special hours for {h.Date:yyyy-MM-dd}."
                    : $"No special hours existed for {h.Date:yyyy-MM-dd}; nothing removed.");
            }
        }

        // Categories created in this same change-set have no id until applied - items that
        // reference one by name (instead of a real CategoryId) are resolved against this map.
        var newCategoryIdsByName = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

        if (request.ChangeSet.MenuCategoryChanges is { Count: > 0 } categoryChanges)
        {
            foreach (var change in categoryChanges)
            {
                await ApplyCategoryChangeAsync(tenantId, change, cancellationToken, appliedSummary, newCategoryIdsByName);
            }
        }

        if (request.ChangeSet.MenuItemChanges is { Count: > 0 } itemChanges)
        {
            foreach (var change in itemChanges)
            {
                await ApplyItemChangeAsync(tenantId, change, cancellationToken, appliedSummary, newCategoryIdsByName);
            }
        }

        if (appliedSummary.Count == 0 && rejectedSummary.Count > 0)
        {
            submission.Status = AgentSubmissionStatus.Rejected;
            submission.RejectionReason = string.Join(" ", rejectedSummary);
            submission.UpdatedUtc = DateTime.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);

            var rejectedOwnerEmail = await GetOwnerEmailAsync(tenantId, cancellationToken);
            if (!string.IsNullOrWhiteSpace(rejectedOwnerEmail))
            {
                await emailService.SendAgentChangesNotAppliedEmailAsync(
                    rejectedOwnerEmail, tenant.Name, rejectedSummary, cancellationToken);
            }

            return Ok(new AgentApplyResponse(null, null, null, appliedSummary, rejectedSummary));
        }

        var rollbackExpiresUtc = DateTime.UtcNow.AddDays(30);
        var audit = new AgentChangeAudit
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            SubmissionId = submission.Id,
            PreChangeSnapshotJson = JsonSerializer.Serialize(preChangeSnapshot, JsonOptions),
            DiffSummaryJson = JsonSerializer.Serialize(appliedSummary, JsonOptions),
            AppliedUtc = DateTime.UtcNow,
            RollbackExpiresUtc = rollbackExpiresUtc,
            RolledBack = false
        };
        dbContext.AgentChangeAudits.Add(audit);

        submission.Status = AgentSubmissionStatus.Applied;
        submission.UpdatedUtc = DateTime.UtcNow;

        await dbContext.SaveChangesAsync(cancellationToken);

        var ownerEmail = await GetOwnerEmailAsync(tenantId, cancellationToken);

        if (!string.IsNullOrWhiteSpace(ownerEmail) && appliedSummary.Count > 0)
        {
            await emailService.SendAgentChangesAppliedEmailAsync(
                ownerEmail, tenant.Name, appliedSummary, rejectedSummary, audit.Id, rollbackExpiresUtc, cancellationToken);
        }

        return Ok(new AgentApplyResponse(audit.Id, audit.AppliedUtc, rollbackExpiresUtc, appliedSummary, rejectedSummary));
    }

    private Task<string?> GetOwnerEmailAsync(Guid tenantId, CancellationToken cancellationToken) =>
        dbContext.TenantUsers
            .Where(x => x.TenantId == tenantId)
            .OrderBy(x => x.Role == "Owner" ? 0 : 1)
            .Select(x => x.Email)
            .FirstOrDefaultAsync(cancellationToken);

    [HttpPost("rollback/{auditId:guid}")]
    public async Task<IActionResult> Rollback(Guid auditId, CancellationToken cancellationToken)
    {
        var tenantId = tenantContext.TenantId!.Value;

        var audit = await dbContext.AgentChangeAudits
            .SingleOrDefaultAsync(x => x.Id == auditId && x.TenantId == tenantId, cancellationToken);
        if (audit is null)
        {
            return NotFound(new { Error = "Audit record not found for this tenant." });
        }

        if (audit.RolledBack)
        {
            return BadRequest(new { Error = "This change has already been rolled back." });
        }

        if (DateTime.UtcNow > audit.RollbackExpiresUtc)
        {
            return BadRequest(new { Error = "The rollback window for this change has expired." });
        }

        var snapshot = JsonSerializer.Deserialize<PublicRestaurantResponse>(audit.PreChangeSnapshotJson, JsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize pre-change snapshot.");

        await restaurantAdminService.RestoreSnapshotAsync(tenantId, snapshot, cancellationToken);

        audit.RolledBack = true;
        audit.RolledBackUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        return NoContent();
    }

    /// <summary>
    /// Stores an email-attached image under a unique R2 key and returns its URL without touching the profile,
    /// so the live site only changes once the URL passes validation and is applied.
    /// </summary>
    [HttpPost("images/{imageType}")]
    [RequestSizeLimit(MaxImageBytes + 1024 * 1024)]
    public async Task<IActionResult> UploadImage(
        string imageType,
        IFormFile file,
        CancellationToken cancellationToken)
    {
        var normalizedImageType = imageType.Trim().ToLowerInvariant();
        if (normalizedImageType is not ("logo" or "hero"))
        {
            return BadRequest(new { Error = "imageType must be one of: logo, hero." });
        }

        var tenantId = tenantContext.TenantId!.Value;
        var tenant = await dbContext.Tenants.AsNoTracking().SingleAsync(x => x.Id == tenantId, cancellationToken);
        if (tenant.SubscriptionPlan != DoneForYouPlan)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                Error = "Tenant is not eligible for agent-applied updates (requires Done-For-You plan)."
            });
        }

        if (file is null || file.Length == 0)
        {
            return BadRequest(new { Error = "No file provided." });
        }

        if (file.Length > MaxImageBytes)
        {
            return BadRequest(new { Error = "File size must not exceed 10 MB." });
        }

        string? contentType;
        await using (var header = file.OpenReadStream())
        {
            contentType = await DetectImageContentTypeAsync(header, cancellationToken);
        }

        if (contentType is null)
        {
            return BadRequest(new { Error = "Only JPEG and PNG images are accepted." });
        }

        try
        {
            await using var stream = file.OpenReadStream();
            var url = await r2StorageService.UploadImageAsync(
                tenantId,
                $"{normalizedImageType}-{Guid.NewGuid():N}",
                stream,
                contentType,
                cancellationToken);

            return Ok(new { url });
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new { Error = "Failed to upload image.", Details = ex.Message });
        }
    }

    private static async Task<string?> DetectImageContentTypeAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[8];
        var read = await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken);

        if (read >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
        {
            return "image/jpeg";
        }

        ReadOnlySpan<byte> pngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        return read >= 8 && header.AsSpan(0, 8).SequenceEqual(pngSignature) ? "image/png" : null;
    }

    private static bool IsAction(string? value, string expected) =>
        string.Equals(value?.Trim(), expected, StringComparison.OrdinalIgnoreCase);

    private static (List<AgentDateSpecificHoursChange> Active, List<string> Rejected, string? Error) ValidateDateSpecificHours(
        IReadOnlyList<AgentDateSpecificHoursChange>? changes)
    {
        var active = new List<AgentDateSpecificHoursChange>();
        var rejected = new List<string>();
        if (changes is null || changes.Count == 0)
        {
            return (active, rejected, null);
        }

        if (changes.Count > 7)
        {
            return (active, rejected, "At most 7 date-specific hours changes are allowed per request.");
        }

        if (changes.Select(c => c.Date).Distinct().Count() != changes.Count)
        {
            return (active, rejected, "Duplicate dates are not allowed in date-specific hours changes.");
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        foreach (var change in changes)
        {
            if (!IsAction(change.Action, "upsert") && !IsAction(change.Action, "delete"))
            {
                return (active, rejected, $"Unknown date-specific hours action '{change.Action}'.");
            }

            if (change.Date < today)
            {
                rejected.Add($"Hours for {change.Date:MMMM d, yyyy} were not changed because that date has already passed. If you meant next year, reply with the year.");
                continue;
            }

            if (IsAction(change.Action, "upsert") && change.IsClosed != true)
            {
                if (change.IsClosed is null || change.OpenTime is null || change.CloseTime is null)
                {
                    return (active, rejected, $"Open time, close time and isClosed are required for {change.Date:yyyy-MM-dd}.");
                }

                if (change.OpenTime >= change.CloseTime)
                {
                    return (active, rejected, $"Open time must be before close time for {change.Date:yyyy-MM-dd}.");
                }
            }

            active.Add(change);
        }

        return (active, rejected, null);
    }

    private async Task ApplyBrandingChangeAsync(
        Guid tenantId,
        AgentBrandingChange branding,
        CancellationToken cancellationToken,
        List<string> summary)
    {
        var profile = await dbContext.RestaurantProfiles
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == tenantId, cancellationToken);
        if (profile is null)
        {
            return;
        }

        var merged = new UpdateBrandingRequest(
            branding.DisplayName ?? profile.DisplayName,
            branding.Tagline ?? profile.Tagline,
            branding.Announcement ?? profile.Announcement,
            branding.PrimaryHexColor ?? profile.PrimaryHexColor,
            branding.SecondaryHexColor ?? profile.SecondaryHexColor,
            branding.LogoUrl ?? profile.LogoUrl,
            branding.HeroImageUrl ?? profile.HeroImageUrl,
            branding.PrimaryCtaUrl ?? profile.PrimaryCtaUrl);

        var updated = await restaurantAdminService.UpdateBrandingAsync(tenantId, merged, cancellationToken);
        if (updated)
        {
            summary.Add("Updated branding/profile information.");
        }
    }

    private async Task ApplyCategoryChangeAsync(
        Guid tenantId,
        AgentMenuCategoryChange change,
        CancellationToken cancellationToken,
        List<string> summary,
        Dictionary<string, Guid> newCategoryIdsByName)
    {
        if (!Enum.TryParse<AgentItemAction>(change.Action, ignoreCase: true, out var action))
        {
            summary.Add($"Skipped menu category change with unknown action '{change.Action}'.");
            return;
        }

        var description = change.Description?.Trim();
        if (description?.Length > 300)
        {
            summary.Add("Skipped menu category change because its description exceeds 300 characters.");
            return;
        }

        switch (action)
        {
            case AgentItemAction.Create:
                var name = change.Name ?? "New Category";
                var createdCategoryId = await restaurantAdminService.CreateMenuCategoryAsync(
                    tenantId, new CreateMenuCategoryRequest(name, change.SortOrder ?? 0, description), cancellationToken);
                newCategoryIdsByName[name] = createdCategoryId;
                summary.Add($"Created menu category '{name}'.");
                break;

            case AgentItemAction.Update:
                if (change.CategoryId is { } categoryId)
                {
                    var current = await dbContext.MenuCategories.AsNoTracking()
                        .SingleOrDefaultAsync(x => x.Id == categoryId && x.TenantId == tenantId, cancellationToken);
                    if (current is null) break;

                    var mergedName = change.Name ?? current.Name;
                    var mergedDescription = description ?? current.Description;
                    var mergedSortOrder = change.SortOrder ?? current.SortOrder;
                    var updated = await restaurantAdminService.UpdateMenuCategoryAsync(
                        tenantId, categoryId, mergedName, mergedDescription, mergedSortOrder, cancellationToken);
                    if (updated) summary.Add($"Updated menu category '{mergedName}'.");
                }
                break;

            case AgentItemAction.Delete:
                if (change.CategoryId is { } deleteCategoryId)
                {
                    var deleted = await restaurantAdminService.DeleteMenuCategoryAsync(tenantId, deleteCategoryId, cancellationToken);
                    if (deleted) summary.Add($"Deleted menu category {deleteCategoryId}.");
                }
                break;
        }
    }

    private async Task ApplyItemChangeAsync(
        Guid tenantId,
        AgentMenuItemChange change,
        CancellationToken cancellationToken,
        List<string> summary,
        IReadOnlyDictionary<string, Guid> newCategoryIdsByName)
    {
        if (!Enum.TryParse<AgentItemAction>(change.Action, ignoreCase: true, out var action))
        {
            summary.Add($"Skipped menu item change with unknown action '{change.Action}'.");
            return;
        }

        switch (action)
        {
            case AgentItemAction.Create:
                // categoryId is set for existing categories; categoryName resolves one created earlier in this same change-set.
                var resolvedCategoryId = change.CategoryId
                    ?? (change.CategoryName is { } categoryName && newCategoryIdsByName.TryGetValue(categoryName, out var newCategoryId)
                        ? newCategoryId
                        : (Guid?)null);

                if (resolvedCategoryId is { } categoryId)
                {
                    var request = new CreateMenuItemRequest(
                        categoryId,
                        change.Name ?? string.Empty,
                        change.Description ?? string.Empty,
                        change.Price ?? 0m,
                        change.IsAvailable ?? true,
                        change.SortOrder ?? 0);

                    var result = await restaurantAdminService.CreateMenuItemAsync(tenantId, request, cancellationToken);
                    if (result.Status == MenuItemMutationStatus.Success)
                    {
                        summary.Add($"Created menu item '{request.Name}' (${request.Price:0.00}).");
                    }
                }
                else
                {
                    summary.Add($"Skipped menu item '{change.Name ?? "unknown"}' - could not resolve its category.");
                }
                break;

            case AgentItemAction.Update:
                if (change.ItemId is { } itemId)
                {
                    var current = await dbContext.MenuItems.AsNoTracking()
                        .SingleOrDefaultAsync(x => x.Id == itemId && x.TenantId == tenantId, cancellationToken);
                    if (current is null) break;

                    var merged = new UpdateMenuItemRequest(
                        change.CategoryId ?? current.CategoryId,
                        change.Name ?? current.Name,
                        change.Description ?? current.Description,
                        change.Price ?? current.Price,
                        change.IsAvailable ?? current.IsAvailable,
                        change.SortOrder ?? current.SortOrder);

                    var result = await restaurantAdminService.UpdateMenuItemAsync(tenantId, itemId, merged, cancellationToken);
                    if (result.Status == MenuItemMutationStatus.Success)
                    {
                        summary.Add($"Updated menu item '{merged.Name}' (${merged.Price:0.00}).");
                    }
                }
                break;

            case AgentItemAction.Delete:
                if (change.ItemId is { } deleteItemId)
                {
                    var deleted = await restaurantAdminService.DeleteMenuItemAsync(tenantId, deleteItemId, cancellationToken);
                    if (deleted) summary.Add($"Deleted menu item {deleteItemId}.");
                }
                break;
        }
    }
}
