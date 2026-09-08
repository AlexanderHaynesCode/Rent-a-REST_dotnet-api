using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RentARestaurant.Api.Contracts;
using RentARestaurant.Api.Data;
using RentARestaurant.Api.Domain.Entities;
using RentARestaurant.Api.Infrastructure.Email;
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
    IEmailService emailService) : ControllerBase
{
    private const string DoneForYouPlan = "Done-For-You";
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

        var ownerEmail = await dbContext.TenantUsers
            .Where(x => x.TenantId == tenantId)
            .OrderBy(x => x.Role == "Owner" ? 0 : 1)
            .Select(x => x.Email)
            .FirstOrDefaultAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(ownerEmail) && appliedSummary.Count > 0)
        {
            await emailService.SendAgentChangesAppliedEmailAsync(
                ownerEmail, tenant.Name, appliedSummary, audit.Id, rollbackExpiresUtc, cancellationToken);
        }

        return Ok(new AgentApplyResponse(audit.Id, audit.AppliedUtc, rollbackExpiresUtc, appliedSummary));
    }

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

        switch (action)
        {
            case AgentItemAction.Create:
                var name = change.Name ?? "New Category";
                var createdCategoryId = await restaurantAdminService.CreateMenuCategoryAsync(
                    tenantId, new CreateMenuCategoryRequest(name, change.SortOrder ?? 0), cancellationToken);
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
                    var mergedSortOrder = change.SortOrder ?? current.SortOrder;
                    var updated = await restaurantAdminService.UpdateMenuCategoryAsync(
                        tenantId, categoryId, mergedName, mergedSortOrder, cancellationToken);
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
