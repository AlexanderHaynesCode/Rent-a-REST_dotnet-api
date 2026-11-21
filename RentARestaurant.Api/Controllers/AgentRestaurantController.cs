using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RentARestaurant.Api.Contracts;
using RentARestaurant.Api.Data;
using RentARestaurant.Api.Domain;
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
    IAgentRollbackService agentRollbackService,
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

        var changeSet = request.ChangeSet;
        var (activeDateHours, rejectedSummary, dateHoursError) = ValidateDateSpecificHours(changeSet.DateSpecificHoursChanges);
        if (dateHoursError is not null)
        {
            return BadRequest(new { Error = dateHoursError });
        }

        // Everything below commits together with the audit record: a failure part-way must not leave
        // changes live that have no snapshot to roll back to.
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var preChangeSnapshot = await restaurantAdminService.GetSnapshotAsync(tenantId, cancellationToken);
        var appliedSummary = new List<string>();

        // Branding, the hours sections' settings and the menu settings all live on the restaurant profile,
        // so they are applied first and described together by comparing the profile before and after.
        var profileChangeRequested = changeSet.BrandingChange is not null
            || changeSet.HoursDisplayChange is not null
            || changeSet.MenuSettingsChange is not null;
        if (profileChangeRequested)
        {
            var profileBefore = await LoadProfileAsync(tenantId, cancellationToken);
            if (profileBefore is not null)
            {
                var rejectedBefore = rejectedSummary.Count;

                if (changeSet.BrandingChange is { } branding)
                {
                    await ApplyBrandingChangeAsync(tenantId, profileBefore, branding, rejectedSummary, cancellationToken);
                }

                if (changeSet.HoursDisplayChange is { } hoursDisplay)
                {
                    await ApplyHoursDisplayChangeAsync(tenantId, hoursDisplay, rejectedSummary, cancellationToken);
                }

                if (changeSet.MenuSettingsChange is { AllowMenuPdfDownload: { } allowMenuPdfDownload })
                {
                    await restaurantAdminService.SetMenuSettingsAsync(tenantId, allowMenuPdfDownload, cancellationToken);
                }

                var profileAfter = await LoadProfileAsync(tenantId, cancellationToken);
                var profileLines = ProfileChangeSummary.Describe(profileBefore, profileAfter!);
                appliedSummary.AddRange(profileLines);

                if (profileLines.Count == 0 && rejectedSummary.Count == rejectedBefore)
                {
                    rejectedSummary.Add("The branding and display settings you asked for already match your site, so nothing was changed.");
                }
            }
        }

        if (changeSet.HoursChanges is { Count: > 0 } hours)
        {
            await ApplyHoursChangesAsync(tenantId, hours, appliedSummary, rejectedSummary, cancellationToken);
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
                if (removed)
                {
                    appliedSummary.Add($"Removed special hours for {h.Date:yyyy-MM-dd}.");
                }
                else
                {
                    rejectedSummary.Add($"No special hours existed for {h.Date:MMMM d, yyyy}, so nothing was removed.");
                }
            }
        }

        // Menus and categories created in this same change-set have no id until applied - later changes
        // that reference one by name (instead of a real id) are resolved against this context.
        var menuContext = new MenuApplyContext();

        if (changeSet.MenuChanges is { Count: > 0 } menuChanges)
        {
            foreach (var change in menuChanges)
            {
                await ApplyMenuChangeAsync(tenantId, change, menuContext, appliedSummary, rejectedSummary, cancellationToken);
            }
        }

        if (changeSet.MenuCategoryChanges is { Count: > 0 } categoryChanges)
        {
            foreach (var change in categoryChanges)
            {
                await ApplyCategoryChangeAsync(tenantId, change, menuContext, appliedSummary, rejectedSummary, cancellationToken);
            }
        }

        if (changeSet.MenuItemChanges is { Count: > 0 } itemChanges)
        {
            foreach (var change in itemChanges)
            {
                await ApplyItemChangeAsync(tenantId, change, menuContext, appliedSummary, rejectedSummary, cancellationToken);
            }
        }

        if (appliedSummary.Count == 0)
        {
            if (rejectedSummary.Count == 0)
            {
                rejectedSummary.Add("We couldn't find anything on your site to change from your request.");
            }

            submission.Status = AgentSubmissionStatus.Rejected;
            submission.RejectionReason = Truncate(string.Join(" ", rejectedSummary), 1000);
            submission.UpdatedUtc = DateTime.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

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
        await transaction.CommitAsync(cancellationToken);

        var ownerEmail = await GetOwnerEmailAsync(tenantId, cancellationToken);

        if (!string.IsNullOrWhiteSpace(ownerEmail))
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

        var result = await agentRollbackService.RollbackAsync(tenantId, auditId, cancellationToken);

        return result.Outcome switch
        {
            AgentRollbackOutcome.RolledBack => NoContent(),
            AgentRollbackOutcome.NotFound => NotFound(new { Error = "Audit record not found for this tenant." }),
            _ => BadRequest(new { Error = result.Reason })
        };
    }

    /// <summary>
    /// Rollback requested by a tenant's email. Unlike <see cref="Rollback(Guid, CancellationToken)"/> this also updates the
    /// submission and emails the owner. A null AuditId means the most recent change that can still be rolled back.
    /// Business outcomes such as "nothing to undo" are a 200 with RolledBack=false so the executor doesn't mark the
    /// submission Failed.
    /// </summary>
    [HttpPost("rollback")]
    public async Task<ActionResult<AgentRollbackResponse>> RollbackFromSubmission(
        [FromBody] RollbackAgentChangeRequest request,
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

        var result = await agentRollbackService.RollbackAsync(tenantId, request.AuditId, cancellationToken);

        submission.Status = result.Succeeded ? AgentSubmissionStatus.Applied : AgentSubmissionStatus.Rejected;
        submission.RejectionReason = result.Succeeded ? submission.RejectionReason : result.Reason;
        submission.UpdatedUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        var ownerEmail = await GetOwnerEmailAsync(tenantId, cancellationToken);
        if (!string.IsNullOrWhiteSpace(ownerEmail))
        {
            if (result.Succeeded)
            {
                await emailService.SendAgentRollbackAppliedEmailAsync(
                    ownerEmail, tenant.Name, result.RevertedSummary, result.OriginalChangeAppliedUtc!.Value, cancellationToken);
            }
            else
            {
                await emailService.SendAgentRollbackNotAppliedEmailAsync(
                    ownerEmail, tenant.Name, result.Reason!, cancellationToken);
            }
        }

        return Ok(new AgentRollbackResponse(
            result.Succeeded, result.AuditId, result.OriginalChangeAppliedUtc, result.RevertedSummary, result.Reason));
    }

    /// <summary>
    /// Stores an email-attached image under a unique R2 key and returns its URL without touching the profile,
    /// so the live site only changes once the URL passes validation and is applied. The "attachment" type stages
    /// an image while the tenant is still being asked which attached file is their logo/hero. A "menu-background"
    /// key starts like the admin portal's menu background images, so the admin portal can clean it up once no
    /// menu uses it.
    /// </summary>
    [HttpPost("images/{imageType}")]
    [RequestSizeLimit(MaxImageBytes + 1024 * 1024)]
    public async Task<IActionResult> UploadImage(
        string imageType,
        IFormFile file,
        CancellationToken cancellationToken)
    {
        var normalizedImageType = imageType.Trim().ToLowerInvariant();
        if (normalizedImageType is not ("logo" or "hero" or "custom-background" or "menu-background" or "attachment"))
        {
            return BadRequest(new { Error = "imageType must be one of: logo, hero, custom-background, menu-background, attachment." });
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

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private static bool IsHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private Task<RestaurantProfile?> LoadProfileAsync(Guid tenantId, CancellationToken cancellationToken) =>
        dbContext.RestaurantProfiles
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == tenantId, cancellationToken);

    /// <summary>Names of menus and categories created earlier in the same change-set, for by-name references.</summary>
    private sealed class MenuApplyContext
    {
        public Dictionary<string, Guid> NewMenuIdsByName { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<(string Name, Guid MenuId, Guid Id)> NewCategories { get; } = [];

        // Menus/categories this change-set tried to create but couldn't. Later changes that only fail because
        // they point at one of these are dropped silently - the root reason was already reported.
        public HashSet<string> SkippedMenuNames { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> SkippedCategoryNames { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

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

                // A closing time earlier than the opening time means the hours run past midnight.
                if (!BusinessHourRules.AreTimesValid(change.OpenTime.Value, change.CloseTime.Value, isClosed: false))
                {
                    rejected.Add($"Hours for {change.Date:MMMM d, yyyy} were not changed. {BusinessHourRules.InvalidTimesMessage($"{change.Date:MMMM d}")}");
                    continue;
                }
            }

            active.Add(change);
        }

        return (active, rejected, null);
    }

    private async Task ApplyHoursChangesAsync(
        Guid tenantId,
        IReadOnlyList<AgentHoursChange> hours,
        List<string> summary,
        List<string> rejected,
        CancellationToken cancellationToken)
    {
        var existing = await dbContext.BusinessHours
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Date == null)
            .ToListAsync(cancellationToken);

        var requests = new List<UpsertBusinessHourRequest>();
        foreach (var hour in hours)
        {
            // A closed day keeps whatever times are stored, the same as unchecking "Closed" in the admin portal expects.
            var stored = existing.FirstOrDefault(x => x.DayOfWeek == hour.DayOfWeek);
            var openTime = hour.OpenTime ?? stored?.OpenTime;
            var closeTime = hour.CloseTime ?? stored?.CloseTime;

            if (!hour.IsClosed && (hour.OpenTime is null || hour.CloseTime is null))
            {
                rejected.Add($"Hours for {hour.DayOfWeek} were not changed because both an opening and a closing time are needed.");
                continue;
            }

            if (!BusinessHourRules.AreTimesValid(openTime ?? default, closeTime ?? default, hour.IsClosed))
            {
                rejected.Add($"Hours for {hour.DayOfWeek} were not changed. {BusinessHourRules.InvalidTimesMessage(hour.DayOfWeek.ToString())}");
                continue;
            }

            requests.Add(new UpsertBusinessHourRequest(hour.DayOfWeek, openTime ?? default, closeTime ?? default, hour.IsClosed));
        }

        if (requests.Count > 0)
        {
            await restaurantAdminService.UpsertHoursAsync(tenantId, requests, cancellationToken);
            summary.Add($"Updated business hours ({requests.Count} day(s)).");
        }
    }

    private async Task ApplyBrandingChangeAsync(
        Guid tenantId,
        RestaurantProfile profile,
        AgentBrandingChange branding,
        List<string> rejected,
        CancellationToken cancellationToken)
    {
        // Image URLs: null leaves the image as is, blank removes it, anything else must be an http(s) URL.
        string? MergeImageUrl(string? requested, string? current, string label, ref bool invalid)
        {
            if (requested is null) return current;
            var trimmed = requested.Trim();
            if (trimmed.Length == 0) return null;
            if (IsHttpUrl(trimmed)) return trimmed;
            rejected.Add($"Your {label} was not changed because we couldn't find the image for it.");
            invalid = true;
            return current;
        }

        var invalidImage = false;
        var logoUrl = MergeImageUrl(branding.LogoUrl, profile.LogoUrl, "logo", ref invalidImage);
        var heroImageUrl = MergeImageUrl(branding.HeroImageUrl, profile.HeroImageUrl, "hero image", ref invalidImage);
        var backgroundImageUrl = MergeImageUrl(
            branding.CustomBackgroundImageUrl, profile.CustomBackgroundImageUrl, "page background image", ref invalidImage);
        var heroImageSet = heroImageUrl is not null && heroImageUrl != profile.HeroImageUrl;
        var backgroundImageSet = backgroundImageUrl is not null && backgroundImageUrl != profile.CustomBackgroundImageUrl;

        var mainColor = NullIfBlank(branding.CustomBackgroundMainHexColor);
        var splashColor1 = NullIfBlank(branding.CustomBackgroundHexColor1);
        var splashColor2 = NullIfBlank(branding.CustomBackgroundHexColor2);
        var splash1Moved = branding.CustomBackgroundPosition1X is not null || branding.CustomBackgroundPosition1Y is not null;
        var splash2Moved = branding.CustomBackgroundPosition2X is not null || branding.CustomBackgroundPosition2Y is not null;

        // Settings implied by the request, so a change is never invisible: a new image or color switches its
        // layer (and the custom background) on, a transparency setting switches the white background on, and
        // a new hero image un-hides the hero. An explicit value from the tenant always wins.
        var mainColorActive = branding.CustomBackgroundMainColorActive ?? (mainColor is not null || profile.CustomBackgroundMainColorActive);
        var splash1Active = branding.CustomBackgroundSplash1Active ?? (splashColor1 is not null || splash1Moved || profile.CustomBackgroundSplash1Active);
        var splash2Active = branding.CustomBackgroundSplash2Active ?? (splashColor2 is not null || splash2Moved || profile.CustomBackgroundSplash2Active);
        var imageActive = branding.CustomBackgroundImageActive ?? (backgroundImageSet || profile.CustomBackgroundImageActive);

        var backgroundLayerTouched = mainColor is not null || splashColor1 is not null || splashColor2 is not null
            || splash1Moved || splash2Moved || backgroundImageSet
            || branding.CustomBackgroundMainColorActive == true || branding.CustomBackgroundSplash1Active == true
            || branding.CustomBackgroundSplash2Active == true || branding.CustomBackgroundImageActive == true;
        var showCustomBackground = branding.ShowCustomBackground ?? (backgroundLayerTouched || profile.ShowCustomBackground);

        var mergedSplashColor1 = splashColor1 ?? profile.CustomBackgroundHexColor1;
        var mergedSplashColor2 = splashColor2 ?? profile.CustomBackgroundHexColor2;
        if (showCustomBackground)
        {
            // An active splash needs a color; one that never had a color is left off instead.
            if (splash1Active && mergedSplashColor1 is null)
            {
                splash1Active = false;
                if (branding.CustomBackgroundSplash1Active == true)
                    rejected.Add("Background splash color 1 was left off because no color has been chosen for it yet.");
            }

            if (splash2Active && mergedSplashColor2 is null)
            {
                splash2Active = false;
                if (branding.CustomBackgroundSplash2Active == true)
                    rejected.Add("Background splash color 2 was left off because no color has been chosen for it yet.");
            }
        }

        var whiteBackground = branding.ContactHoursWhiteBackground
            ?? (branding.ContactHoursWhiteBackgroundTransparency is not null
                || branding.ApplyTransparencyToMenuItems == true
                || profile.ContactHoursWhiteBackground);

        var merged = new UpdateBrandingRequest(
            NullIfBlank(branding.DisplayName) ?? profile.DisplayName,
            branding.Tagline?.Trim() ?? profile.Tagline,
            branding.Announcement?.Trim() ?? profile.Announcement,
            NullIfBlank(branding.PrimaryHexColor) ?? profile.PrimaryHexColor,
            NullIfBlank(branding.SecondaryHexColor) ?? profile.SecondaryHexColor,
            logoUrl,
            heroImageUrl,
            branding.PrimaryCtaUrl?.Trim() ?? profile.PrimaryCtaUrl,
            branding.Address is null ? profile.Address : NullIfBlank(branding.Address),
            branding.PhoneNumber is null ? profile.PhoneNumber : NullIfBlank(branding.PhoneNumber),
            showCustomBackground,
            mergedSplashColor1,
            mergedSplashColor2,
            branding.CustomBackgroundPosition1X ?? profile.CustomBackgroundPosition1X,
            branding.CustomBackgroundPosition1Y ?? profile.CustomBackgroundPosition1Y,
            branding.CustomBackgroundPosition2X ?? profile.CustomBackgroundPosition2X,
            branding.CustomBackgroundPosition2Y ?? profile.CustomBackgroundPosition2Y,
            mainColor ?? profile.CustomBackgroundMainHexColor,
            mainColorActive,
            splash1Active,
            splash2Active,
            imageActive,
            ShowTopContactSection: branding.ShowTopContactSection ?? profile.ShowTopContactSection,
            ShowBottomContactSection: branding.ShowBottomContactSection ?? profile.ShowBottomContactSection,
            ContactHoursWhiteBackground: whiteBackground,
            ContactHoursWhiteBackgroundTransparency: branding.ContactHoursWhiteBackgroundTransparency ?? profile.ContactHoursWhiteBackgroundTransparency,
            ApplyTransparencyToMenuItems: branding.ApplyTransparencyToMenuItems ?? profile.ApplyTransparencyToMenuItems,
            HideHeroImage: branding.HideHeroImage ?? (!heroImageSet && profile.HideHeroImage));

        string? error = null;
        if (!BrandingRules.IsHexColor(merged.PrimaryHexColor) || !BrandingRules.IsHexColor(merged.SecondaryHexColor))
        {
            error = "Primary and secondary colors must be hex colors like #1A2B3C.";
        }
        else if (merged.DisplayName.Length > 200)
        {
            error = "Display name must be 200 characters or fewer.";
        }
        else if (merged.Tagline.Length > 300 || merged.Announcement.Length > 300)
        {
            error = "Tagline and announcement must be 300 characters or fewer.";
        }

        error ??= BrandingRules.Validate(merged);
        if (error is not null)
        {
            rejected.Add($"Your branding changes were not applied. {error}");
            return;
        }

        await restaurantAdminService.UpdateBrandingAsync(tenantId, merged, cancellationToken);

        // The page background image URL is not part of a branding update (the admin portal sets it on upload).
        if (backgroundImageUrl != profile.CustomBackgroundImageUrl)
        {
            await restaurantAdminService.SetCustomBackgroundImageUrlAsync(tenantId, backgroundImageUrl, cancellationToken);
        }
    }

    private async Task ApplyHoursDisplayChangeAsync(
        Guid tenantId,
        AgentHoursDisplayChange change,
        List<string> rejected,
        CancellationToken cancellationToken)
    {
        var profile = await LoadProfileAsync(tenantId, cancellationToken);
        if (profile is null)
        {
            return;
        }

        var style = NullIfBlank(change.TopHoursStyle)?.ToLowerInvariant();
        var detail = NullIfBlank(change.TopHoursOpenStatusDetail)?.ToLowerInvariant();
        if ((style is not null && !TopHoursOptions.Styles.Contains(style)) ||
            (detail is not null && !TopHoursOptions.OpenStatusDetails.Contains(detail)))
        {
            rejected.Add("Your hours section settings were not changed because we couldn't tell which hours style you wanted.");
            return;
        }

        if (change.ShowTopHoursSection is not null || style is not null || detail is not null || change.UseLegacyHoursStyle is not null)
        {
            // Picking a style for the top section implies showing it, and the sub-options imply their style,
            // mirroring how the admin portal nests these options.
            var showsHoursDetail = detail is not null && detail != TopHoursOptions.NoDetail;
            var mergedStyle = style
                ?? (showsHoursDetail ? TopHoursOptions.OpenStatusStyle
                    : change.UseLegacyHoursStyle == true ? TopHoursOptions.StandardStyle
                    : profile.TopHoursStyle);
            var show = change.ShowTopHoursSection
                ?? (style is not null || showsHoursDetail || change.UseLegacyHoursStyle == true || profile.ShowTopHoursSection);

            await restaurantAdminService.SetTopHoursSectionAsync(
                tenantId,
                new SetTopHoursSectionRequest(
                    show,
                    mergedStyle,
                    detail ?? profile.TopHoursOpenStatusDetail,
                    change.UseLegacyHoursStyle ?? profile.UseLegacyHoursStyle),
                cancellationToken);
        }

        if (change.ShowBottomHoursSection is not null || change.UseLegacyBottomHoursStyle is not null)
        {
            await restaurantAdminService.SetBottomHoursSectionAsync(
                tenantId,
                change.ShowBottomHoursSection ?? (change.UseLegacyBottomHoursStyle == true || profile.ShowBottomHoursSection),
                change.UseLegacyBottomHoursStyle ?? profile.UseLegacyBottomHoursStyle,
                cancellationToken);
        }
    }

    /// <summary>An explicit id wins; otherwise the name is matched against menus created in this change-set, then existing ones.</summary>
    private async Task<Guid?> ResolveMenuIdAsync(
        Guid tenantId,
        Guid? menuId,
        string? menuName,
        MenuApplyContext context,
        CancellationToken cancellationToken)
    {
        if (menuId is not null)
        {
            return menuId;
        }

        var name = NullIfBlank(menuName);
        if (name is null)
        {
            return null;
        }

        if (context.NewMenuIdsByName.TryGetValue(name, out var newMenuId))
        {
            return newMenuId;
        }

        var menus = await dbContext.Menus.AsNoTracking()
            .Where(x => x.TenantId == tenantId)
            .Select(x => new { x.Id, x.Name })
            .ToListAsync(cancellationToken);
        var matches = menus.Where(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count == 1 ? matches[0].Id : null;
    }

    private async Task ApplyMenuChangeAsync(
        Guid tenantId,
        AgentMenuChange change,
        MenuApplyContext context,
        List<string> summary,
        List<string> rejected,
        CancellationToken cancellationToken)
    {
        // "clone" only exists for whole menus, so it is not part of AgentItemAction (shared with categories and items).
        var isClone = IsAction(change.Action, "clone");
        var action = default(AgentItemAction);
        if (!isClone && !Enum.TryParse(change.Action, ignoreCase: true, out action))
        {
            rejected.Add($"Skipped a menu change with unknown action '{change.Action}'.");
            return;
        }

        var name = NullIfBlank(change.Name);
        var description = change.Description?.Trim();
        if (name?.Length > MenuLimits.MaxNameLength)
        {
            rejected.Add($"Skipped the menu '{name}' - menu names must be {MenuLimits.MaxNameLength} characters or fewer.");
            return;
        }

        if (description?.Length > MenuLimits.MaxDescriptionLength)
        {
            rejected.Add($"Skipped a menu change because its description exceeds {MenuLimits.MaxDescriptionLength} characters.");
            return;
        }

        if (isClone)
        {
            await ApplyMenuCloneAsync(tenantId, change, name, description, context, summary, rejected, cancellationToken);
            return;
        }

        if (action == AgentItemAction.Create)
        {
            if (name is null)
            {
                rejected.Add("Skipped creating a menu because it has no name.");
                return;
            }

            var created = await restaurantAdminService.CreateMenuAsync(
                tenantId,
                new CreateMenuRequest(name, change.SortOrder ?? 0, description, change.IsVisible ?? true),
                cancellationToken);
            switch (created.Status)
            {
                case MenuMutationStatus.Success:
                    context.NewMenuIdsByName[name] = created.Id!.Value;
                    summary.Add(change.IsVisible == false
                        ? $"Created the menu '{name}' (hidden from your site)."
                        : $"Created the menu '{name}'.");
                    if (change.HasStyleFields)
                    {
                        await ApplyMenuStyleAsync(tenantId, created.Id.Value, change, summary, rejected, cancellationToken);
                    }
                    break;
                case MenuMutationStatus.LimitReached:
                    context.SkippedMenuNames.Add(name);
                    rejected.Add($"Skipped creating the menu '{name}' - the {MenuLimits.MaxMenusPerTenant}-menu limit was reached.");
                    break;
                case MenuMutationStatus.DuplicateName:
                    // Later changes in this request may still refer to the menu by name.
                    rejected.Add($"Skipped creating the menu '{name}' - a menu with that name already exists.");
                    break;
            }

            return;
        }

        var current = change.MenuId is { } menuId
            ? await dbContext.Menus.AsNoTracking().SingleOrDefaultAsync(x => x.Id == menuId && x.TenantId == tenantId, cancellationToken)
            : null;
        if (current is null)
        {
            rejected.Add($"Skipped a change to the menu '{name ?? "unknown"}' because that menu could not be found.");
            return;
        }

        if (action == AgentItemAction.Update)
        {
            // A background/text-shadow-only change leaves the rest of the menu alone (and isn't reported as
            // "Updated the menu"); a change with nothing at all in it keeps reporting that, as before.
            var changesMenuFields = name is not null || description is not null || change.SortOrder is not null || change.IsVisible is not null;
            if (changesMenuFields || !change.HasStyleFields)
            {
                await ApplyMenuFieldsUpdateAsync(tenantId, current, name, description, change, summary, rejected, cancellationToken);
            }

            if (change.HasStyleFields)
            {
                await ApplyMenuStyleAsync(tenantId, current.Id, change, summary, rejected, cancellationToken);
            }

            return;
        }

        // Deleting a menu deletes its categories and items, so it only happens once the tenant confirmed it.
        if (change.DeleteConfirmed != true)
        {
            rejected.Add($"The menu '{current.Name}' was not deleted because the deletion was not confirmed.");
            return;
        }

        var categoryIds = await dbContext.MenuCategories.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.MenuId == current.Id)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
        var itemCount = await dbContext.MenuItems.AsNoTracking()
            .CountAsync(x => x.TenantId == tenantId && categoryIds.Contains(x.CategoryId), cancellationToken);

        var deleted = await restaurantAdminService.DeleteMenuAsync(tenantId, current.Id, cancellationToken);
        if (deleted.Status == MenuMutationStatus.Success)
        {
            summary.Add($"Deleted the menu '{current.Name}' along with its {categoryIds.Count} category(ies) and {itemCount} item(s).");
        }
        else if (deleted.Status == MenuMutationStatus.LastMenu)
        {
            rejected.Add($"The menu '{current.Name}' was not deleted because your site needs at least one menu.");
        }
        else
        {
            rejected.Add($"The menu '{current.Name}' could not be deleted.");
        }
    }

    /// <summary>Renames, re-describes, re-orders or shows/hides the menu <paramref name="current"/>.</summary>
    private async Task ApplyMenuFieldsUpdateAsync(
        Guid tenantId,
        Menu current,
        string? name,
        string? description,
        AgentMenuChange change,
        List<string> summary,
        List<string> rejected,
        CancellationToken cancellationToken)
    {
        var merged = new UpdateMenuRequest(
            name ?? current.Name,
            description ?? current.Description,
            change.SortOrder ?? current.SortOrder,
            change.IsVisible ?? current.IsVisible);
        var updated = await restaurantAdminService.UpdateMenuAsync(tenantId, current.Id, merged, cancellationToken);
        if (updated.Status == MenuMutationStatus.DuplicateName)
        {
            rejected.Add($"Skipped renaming the menu '{current.Name}' to '{merged.Name}' - a menu with that name already exists.");
            return;
        }

        if (updated.Status != MenuMutationStatus.Success)
        {
            rejected.Add($"The menu '{current.Name}' could not be updated.");
            return;
        }

        var lines = new List<string>();
        if (merged.Name != current.Name) lines.Add($"Renamed the menu '{current.Name}' to '{merged.Name}'.");
        if (merged.IsVisible != current.IsVisible)
        {
            lines.Add(merged.IsVisible
                ? $"Made the menu '{merged.Name}' visible on your site."
                : $"Hid the menu '{merged.Name}' from your site.");
        }

        if (merged.Description != current.Description || merged.SortOrder != current.SortOrder || lines.Count == 0)
        {
            lines.Add($"Updated the menu '{merged.Name}'.");
        }

        summary.AddRange(lines);
    }

    /// <summary>
    /// Applies a menu change's background and text shadow fields (the admin portal's "Add a background to this
    /// menu" and "Add a text shadow to this menu's text") to the menu <paramref name="menuId"/>: an existing menu,
    /// or one the same change just created or copied. Settings the tenant didn't mention are implied so a change is
    /// never invisible: a new image, color or pattern picks its background style, a shadow value turns the shadow
    /// on, and a changed background or shadow is shown on the site. An explicit value from the tenant always wins.
    /// The background and the text shadow are checked separately, so a problem with one doesn't block the other.
    /// </summary>
    private async Task ApplyMenuStyleAsync(
        Guid tenantId,
        Guid menuId,
        AgentMenuChange change,
        List<string> summary,
        List<string> rejected,
        CancellationToken cancellationToken)
    {
        var before = await dbContext.Menus.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == menuId && x.TenantId == tenantId, cancellationToken);
        if (before is null)
        {
            return;
        }

        var rejectedBefore = rejected.Count;

        // Background. Image URL: null leaves the image as is, blank removes it, anything else must be an http(s) URL.
        string? backgroundError = null;
        var imageUrl = before.BackgroundImageUrl;
        var newImage = false;
        if (change.BackgroundImageUrl is { } requestedImage)
        {
            var trimmed = requestedImage.Trim();
            if (trimmed.Length == 0)
            {
                imageUrl = null;
            }
            else if (IsHttpUrl(trimmed))
            {
                newImage = trimmed != before.BackgroundImageUrl;
                imageUrl = trimmed;
            }
            else
            {
                backgroundError = "We couldn't find the image you attached for it.";
            }
        }

        var requestedStyle = NullIfBlank(change.BackgroundStyle)?.ToLowerInvariant();
        var hexColor = NullIfBlank(change.BackgroundHexColor);
        var pattern = NullIfBlank(change.BackgroundPattern)?.ToLowerInvariant();
        var style = requestedStyle
            ?? (newImage ? MenuBackgroundRules.ImageStyle
                : hexColor is not null ? MenuBackgroundRules.ColorStyle
                : pattern is not null ? MenuBackgroundRules.PatternStyle
                : before.BackgroundStyle);

        backgroundError ??= MenuBackgroundRules.Validate(style, hexColor, pattern);
        if (backgroundError is null && style != before.BackgroundStyle)
        {
            // A newly picked style needs its value, already stored or sent with it (the admin portal's form
            // always sends the picker's color/pattern).
            if (style == MenuBackgroundRules.ColorStyle && (hexColor ?? before.BackgroundHexColor) is null)
                backgroundError = "No color has been chosen for it yet.";
            else if (style == MenuBackgroundRules.PatternStyle && (pattern ?? before.BackgroundPattern) is null)
                backgroundError = "No pattern has been chosen for it yet.";
            else if (style == MenuBackgroundRules.ImageStyle && imageUrl is null)
                backgroundError = "It doesn't have an image yet, so please reply with the image attached.";
        }

        var backgroundTouched = (requestedStyle is not null && requestedStyle != MenuBackgroundRules.NoneStyle)
            || hexColor is not null || pattern is not null || newImage;
        var showBackground = change.ShowBackgroundOnSite
            ?? ((backgroundTouched && style != MenuBackgroundRules.NoneStyle) || before.ShowBackgroundOnSite);

        if (backgroundError is not null)
        {
            rejected.Add($"The background of the menu '{before.Name}' was not changed. {backgroundError}");
        }

        // Text shadow.
        var shadowColor = NullIfBlank(change.TextShadowHexColor);
        var shadowError = MenuTextShadowRules.Validate(change.TextShadowOffsetX, change.TextShadowOffsetY, change.TextShadowBlur, shadowColor);
        var shadowValuesSent = change.TextShadowOffsetX is not null || change.TextShadowOffsetY is not null
            || change.TextShadowBlur is not null || shadowColor is not null;
        var shadowEnabled = change.TextShadowEnabled ?? (shadowValuesSent || before.TextShadowEnabled);
        var shadowTouched = change.TextShadowEnabled == true || shadowValuesSent;
        var showShadow = change.ShowTextShadowOnSite ?? ((shadowTouched && shadowEnabled) || before.ShowTextShadowOnSite);

        if (shadowError is not null)
        {
            rejected.Add($"The text shadow of the menu '{before.Name}' was not changed. {shadowError}");
        }

        var applyBackground = backgroundError is null && change.HasBackgroundFields;
        var applyShadow = shadowError is null && change.HasTextShadowFields;
        if (applyBackground || applyShadow)
        {
            var request = new UpdateMenuRequest(
                before.Name,
                before.Description,
                before.SortOrder,
                before.IsVisible,
                BackgroundStyle: applyBackground ? style : null,
                BackgroundHexColor: applyBackground ? hexColor : null,
                BackgroundPattern: applyBackground ? pattern : null,
                ShowBackgroundOnSite: applyBackground ? showBackground : null,
                TextShadowEnabled: applyShadow ? shadowEnabled : null,
                TextShadowOffsetX: applyShadow ? change.TextShadowOffsetX : null,
                TextShadowOffsetY: applyShadow ? change.TextShadowOffsetY : null,
                TextShadowBlur: applyShadow ? change.TextShadowBlur : null,
                TextShadowHexColor: applyShadow ? shadowColor : null,
                ShowTextShadowOnSite: applyShadow ? showShadow : null);
            var updated = await restaurantAdminService.UpdateMenuAsync(tenantId, menuId, request, cancellationToken);
            if (updated.Status != MenuMutationStatus.Success)
            {
                rejected.Add($"The background and text shadow of the menu '{before.Name}' could not be changed.");
                return;
            }

            // The image is not part of a menu update (the admin portal sets it on upload). The old image stays in
            // storage so this change can still be rolled back.
            if (applyBackground && imageUrl != before.BackgroundImageUrl)
            {
                await restaurantAdminService.SetMenuBackgroundImageUrlAsync(tenantId, menuId, imageUrl, cancellationToken);
            }
        }

        var after = await dbContext.Menus.AsNoTracking()
            .SingleAsync(x => x.Id == menuId && x.TenantId == tenantId, cancellationToken);
        var lines = MenuStyleChangeSummary.Describe(before, after);
        summary.AddRange(lines);

        if (lines.Count == 0 && rejected.Count == rejectedBefore)
        {
            rejected.Add($"The background and text shadow settings you asked for on the menu '{after.Name}' already match your site, so they were not changed.");
        }
    }

    /// <summary>
    /// Copies the menu <see cref="AgentMenuChange.MenuId"/> into a new menu named <paramref name="name"/> (null: the
    /// next free "<menu name>_copy"). The copy is registered by name so later category/item changes in the same
    /// change-set can add to it through MenuName.
    /// </summary>
    private async Task ApplyMenuCloneAsync(
        Guid tenantId,
        AgentMenuChange change,
        string? name,
        string? description,
        MenuApplyContext context,
        List<string> summary,
        List<string> rejected,
        CancellationToken cancellationToken)
    {
        if (change.MenuId is not { } sourceMenuId)
        {
            if (name is not null) context.SkippedMenuNames.Add(name);
            rejected.Add("Skipped copying a menu because we couldn't tell which menu to copy.");
            return;
        }

        var result = await restaurantAdminService.CloneMenuAsync(
            tenantId,
            sourceMenuId,
            new CloneMenuRequest(name, description, change.SortOrder, change.IsVisible),
            cancellationToken);

        if (result.Status != MenuCloneStatus.Success)
        {
            if (name is not null)
            {
                context.SkippedMenuNames.Add(name);
            }
            else if (result.SourceName is not null)
            {
                context.SkippedMenuNames.Add($"{result.SourceName}{MenuCloneRules.CopySuffix}");
            }
        }

        switch (result.Status)
        {
            case MenuCloneStatus.Success:
                var clone = result.Copy!;
                context.NewMenuIdsByName[clone.Menu.Name] = clone.Menu.Id;
                if (name is null)
                {
                    // The translator refers to an unnamed copy as "<menu name>_copy", even when an older menu
                    // already had that name and this copy became "<menu name>_copy_2".
                    context.NewMenuIdsByName.TryAdd($"{result.SourceName}{MenuCloneRules.CopySuffix}", clone.Menu.Id);
                }

                var itemCount = clone.Categories.Sum(c => c.Items.Count);
                var hidden = clone.Menu.IsVisible ? "" : " (hidden from your site until you make it visible)";
                summary.Add(
                    $"Copied the menu '{result.SourceName}' to a new menu '{clone.Menu.Name}' with its " +
                    $"{clone.Categories.Count} category(ies) and {itemCount} item(s){hidden}.");
                if (change.HasStyleFields)
                {
                    await ApplyMenuStyleAsync(tenantId, clone.Menu.Id, change, summary, rejected, cancellationToken);
                }
                break;
            case MenuCloneStatus.LimitReached:
                rejected.Add($"Skipped copying the menu '{result.SourceName}'. {string.Join(" ", result.LimitProblems!)}");
                break;
            case MenuCloneStatus.DuplicateName:
                rejected.Add($"Skipped copying the menu '{result.SourceName}' - a menu named '{name}' already exists.");
                break;
            default:
                rejected.Add("Skipped copying a menu because the menu to copy could not be found.");
                break;
        }
    }

    private async Task ApplyCategoryChangeAsync(
        Guid tenantId,
        AgentMenuCategoryChange change,
        MenuApplyContext context,
        List<string> summary,
        List<string> rejected,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<AgentItemAction>(change.Action, ignoreCase: true, out var action))
        {
            rejected.Add($"Skipped menu category change with unknown action '{change.Action}'.");
            return;
        }

        var description = change.Description?.Trim();
        if (description?.Length > MenuLimits.MaxDescriptionLength)
        {
            rejected.Add($"Skipped menu category change because its description exceeds {MenuLimits.MaxDescriptionLength} characters.");
            return;
        }

        var requestedName = NullIfBlank(change.Name);
        if (requestedName?.Length > MenuLimits.MaxNameLength)
        {
            rejected.Add($"Skipped the menu category '{requestedName}' - category names must be {MenuLimits.MaxNameLength} characters or fewer.");
            return;
        }

        // Null means "the default menu" for a create and "stay in the current menu" for an update.
        var targetMenuId = await ResolveMenuIdAsync(tenantId, change.MenuId, change.MenuName, context, cancellationToken);
        if (targetMenuId is null && NullIfBlank(change.MenuName) is { } unknownMenuName && action != AgentItemAction.Delete)
        {
            if (context.SkippedMenuNames.Contains(unknownMenuName))
            {
                // Its menu was skipped earlier in this request and that reason is already reported.
                if (action == AgentItemAction.Create && requestedName is not null)
                {
                    context.SkippedCategoryNames.Add(requestedName);
                }

                return;
            }

            rejected.Add($"Skipped the menu category '{requestedName ?? "unknown"}' - the menu '{unknownMenuName}' could not be found.");
            return;
        }

        switch (action)
        {
            case AgentItemAction.Create:
                var name = requestedName ?? "New Category";
                var created = await restaurantAdminService.CreateMenuCategoryAsync(
                    tenantId, new CreateMenuCategoryRequest(name, change.SortOrder ?? 0, description, targetMenuId), cancellationToken);
                switch (created.Status)
                {
                    case MenuMutationStatus.Success:
                        var createdMenuId = await dbContext.MenuCategories.AsNoTracking()
                            .Where(x => x.Id == created.Id!.Value)
                            .Select(x => x.MenuId)
                            .SingleAsync(cancellationToken);
                        context.NewCategories.Add((name, createdMenuId, created.Id!.Value));
                        summary.Add($"Created menu category '{name}'.");
                        break;
                    case MenuMutationStatus.LimitReached:
                        context.SkippedCategoryNames.Add(name);
                        rejected.Add($"Skipped creating menu category '{name}' - the {MenuLimits.MaxCategoriesPerTenant}-category limit was reached.");
                        break;
                    case MenuMutationStatus.DuplicateName:
                        // The category already exists, so later items that name it still resolve to it.
                        rejected.Add($"Skipped creating menu category '{name}' - that menu already has a category with that name.");
                        break;
                    case MenuMutationStatus.MenuInvalid:
                        context.SkippedCategoryNames.Add(name);
                        rejected.Add($"Skipped creating menu category '{name}' - its menu could not be found.");
                        break;
                }
                break;

            case AgentItemAction.Update:
                var current = change.CategoryId is { } categoryId
                    ? await dbContext.MenuCategories.AsNoTracking()
                        .SingleOrDefaultAsync(x => x.Id == categoryId && x.TenantId == tenantId, cancellationToken)
                    : null;
                if (current is null)
                {
                    rejected.Add($"Skipped a change to the menu category '{requestedName ?? "unknown"}' because that category could not be found.");
                    break;
                }

                var mergedName = requestedName ?? current.Name;
                var updated = await restaurantAdminService.UpdateMenuCategoryAsync(
                    tenantId,
                    current.Id,
                    mergedName,
                    description ?? current.Description,
                    change.SortOrder ?? current.SortOrder,
                    targetMenuId,
                    cancellationToken);
                switch (updated.Status)
                {
                    case MenuMutationStatus.Success:
                        summary.Add(targetMenuId is { } movedTo && movedTo != current.MenuId
                            ? $"Moved menu category '{mergedName}' to another menu."
                            : $"Updated menu category '{mergedName}'.");
                        break;
                    case MenuMutationStatus.DuplicateName:
                        rejected.Add($"Skipped the change to menu category '{mergedName}' - that menu already has a category with that name.");
                        break;
                    case MenuMutationStatus.MenuInvalid:
                        rejected.Add($"Skipped moving menu category '{mergedName}' - its new menu could not be found.");
                        break;
                    default:
                        rejected.Add($"The menu category '{mergedName}' could not be updated.");
                        break;
                }
                break;

            case AgentItemAction.Delete:
                var deleteCategoryName = change.CategoryId is { } deleteCategoryId
                    ? await dbContext.MenuCategories.AsNoTracking()
                        .Where(x => x.Id == deleteCategoryId && x.TenantId == tenantId)
                        .Select(x => x.Name)
                        .SingleOrDefaultAsync(cancellationToken)
                    : null;
                if (deleteCategoryName is not null
                    && await restaurantAdminService.DeleteMenuCategoryAsync(tenantId, change.CategoryId!.Value, cancellationToken))
                {
                    summary.Add($"Deleted menu category '{deleteCategoryName}'.");
                }
                else
                {
                    rejected.Add($"Skipped deleting the menu category '{requestedName ?? "unknown"}' because it could not be found.");
                }
                break;
        }
    }

    /// <summary>
    /// The category a new item goes into: an explicit id, or else the category with that name, preferring
    /// one created earlier in this change-set. Null when there is no match or more than one.
    /// </summary>
    private async Task<Guid?> ResolveItemCategoryIdAsync(
        Guid tenantId,
        AgentMenuItemChange change,
        MenuApplyContext context,
        CancellationToken cancellationToken)
    {
        if (change.CategoryId is not null)
        {
            return change.CategoryId;
        }

        var categoryName = NullIfBlank(change.CategoryName);
        if (categoryName is null)
        {
            return null;
        }

        var menuId = await ResolveMenuIdAsync(tenantId, change.MenuId, change.MenuName, context, cancellationToken);

        var created = context.NewCategories
            .Where(x => string.Equals(x.Name, categoryName, StringComparison.OrdinalIgnoreCase) && (menuId is null || x.MenuId == menuId))
            .ToList();
        if (created.Count > 0)
        {
            return created.Count == 1 ? created[0].Id : null;
        }

        var existing = await dbContext.MenuCategories.AsNoTracking()
            .Where(x => x.TenantId == tenantId)
            .Select(x => new { x.Id, x.Name, x.MenuId })
            .ToListAsync(cancellationToken);
        var matches = existing
            .Where(x => string.Equals(x.Name, categoryName, StringComparison.OrdinalIgnoreCase) && (menuId is null || x.MenuId == menuId))
            .ToList();
        return matches.Count == 1 ? matches[0].Id : null;
    }

    private async Task ApplyItemChangeAsync(
        Guid tenantId,
        AgentMenuItemChange change,
        MenuApplyContext context,
        List<string> summary,
        List<string> rejected,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<AgentItemAction>(change.Action, ignoreCase: true, out var action))
        {
            rejected.Add($"Skipped menu item change with unknown action '{change.Action}'.");
            return;
        }

        var requestedName = NullIfBlank(change.Name);
        if (change.Price < 0)
        {
            rejected.Add($"Skipped menu item '{requestedName ?? "unknown"}' - a price can't be negative.");
            return;
        }

        switch (action)
        {
            case AgentItemAction.Create:
                if (requestedName is null)
                {
                    rejected.Add("Skipped a new menu item because it has no name.");
                    break;
                }

                if (await ResolveItemCategoryIdAsync(tenantId, change, context, cancellationToken) is not { } categoryId)
                {
                    var parentSkipped = change.CategoryId is null
                        && ((NullIfBlank(change.CategoryName) is { } skippedCategory && context.SkippedCategoryNames.Contains(skippedCategory))
                            || (NullIfBlank(change.MenuName) is { } skippedMenu && context.SkippedMenuNames.Contains(skippedMenu)));
                    if (parentSkipped)
                    {
                        // Its category or menu was skipped earlier in this request and that reason is already reported.
                        break;
                    }

                    rejected.Add($"Skipped menu item '{requestedName}' - could not resolve its category.");
                    break;
                }

                var request = new CreateMenuItemRequest(
                    categoryId,
                    requestedName,
                    change.Description?.Trim() ?? string.Empty,
                    change.Price ?? 0m,
                    change.IsAvailable ?? true,
                    change.SortOrder ?? 0);

                var result = await restaurantAdminService.CreateMenuItemAsync(tenantId, request, cancellationToken);
                switch (result.Status)
                {
                    case MenuItemMutationStatus.Success:
                        summary.Add($"Created menu item '{request.Name}' (${request.Price:0.00}).");
                        break;
                    case MenuItemMutationStatus.LimitReached:
                        rejected.Add($"Skipped menu item '{request.Name}' - the {MenuLimits.MaxItemsPerTenant}-item limit was reached.");
                        break;
                    default:
                        rejected.Add($"Skipped menu item '{request.Name}' - could not resolve its category.");
                        break;
                }
                break;

            case AgentItemAction.Update:
                var current = change.ItemId is { } itemId
                    ? await dbContext.MenuItems.AsNoTracking()
                        .SingleOrDefaultAsync(x => x.Id == itemId && x.TenantId == tenantId, cancellationToken)
                    : null;
                if (current is null)
                {
                    rejected.Add($"Skipped a change to the menu item '{requestedName ?? "unknown"}' because that item could not be found.");
                    break;
                }

                var merged = new UpdateMenuItemRequest(
                    change.CategoryId ?? current.CategoryId,
                    requestedName ?? current.Name,
                    change.Description?.Trim() ?? current.Description,
                    change.Price ?? current.Price,
                    change.IsAvailable ?? current.IsAvailable,
                    change.SortOrder ?? current.SortOrder);

                var updateResult = await restaurantAdminService.UpdateMenuItemAsync(tenantId, current.Id, merged, cancellationToken);
                if (updateResult.Status == MenuItemMutationStatus.Success)
                {
                    summary.Add($"Updated menu item '{merged.Name}' (${merged.Price:0.00}).");
                }
                else
                {
                    rejected.Add($"Skipped the change to menu item '{merged.Name}' - its category could not be found.");
                }
                break;

            case AgentItemAction.Delete:
                var deleteItemName = change.ItemId is { } deleteItemId
                    ? await dbContext.MenuItems.AsNoTracking()
                        .Where(x => x.Id == deleteItemId && x.TenantId == tenantId)
                        .Select(x => x.Name)
                        .SingleOrDefaultAsync(cancellationToken)
                    : null;
                if (deleteItemName is not null
                    && await restaurantAdminService.DeleteMenuItemAsync(tenantId, change.ItemId!.Value, cancellationToken))
                {
                    summary.Add($"Deleted menu item '{deleteItemName}'.");
                }
                else
                {
                    rejected.Add($"Skipped deleting the menu item '{requestedName ?? "unknown"}' because it could not be found.");
                }
                break;
        }
    }
}
