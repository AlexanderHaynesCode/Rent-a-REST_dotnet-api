namespace RentARestaurant.Api.Contracts;

public enum AgentItemAction
{
    Create,
    Update,
    Delete
}

/// <summary>
/// Action is "create", "update", "delete" or "clone". DeleteConfirmed must be true for a delete: deleting a menu
/// removes its categories and items, so the tenant is asked to confirm first (agent-validator-service raises the
/// question). A "clone" copies the menu MenuId, with all of its categories and items, into a new menu called Name
/// (null: "<menu name>_copy"). The copy starts hidden unless IsVisible is true; Description and SortOrder
/// override the copied values when set.
/// </summary>
public sealed record AgentMenuChange(
    string Action,
    Guid? MenuId,
    string? Name,
    string? Description = null,
    int? SortOrder = null,
    bool? IsVisible = null,
    bool? DeleteConfirmed = null);

/// <summary>
/// MenuId picks an existing menu (on an update it moves the category); MenuName picks a menu by name,
/// including one created earlier in the same change-set. Both null means the tenant's default menu.
/// </summary>
public sealed record AgentMenuCategoryChange(
    string Action,
    Guid? CategoryId,
    string? Name,
    int? SortOrder,
    string? Description = null,
    Guid? MenuId = null,
    string? MenuName = null);

/// <summary>MenuId/MenuName only tell same-named categories in different menus apart when CategoryName is used.</summary>
public sealed record AgentMenuItemChange(
    string Action,
    Guid? ItemId,
    Guid? CategoryId,
    string? CategoryName,
    string? Name,
    string? Description,
    decimal? Price,
    bool? IsAvailable,
    int? SortOrder,
    Guid? MenuId = null,
    string? MenuName = null);

/// <summary>Times may be null for a closed day; the day's stored times are kept.</summary>
public sealed record AgentHoursChange(
    DayOfWeek DayOfWeek,
    TimeOnly? OpenTime,
    TimeOnly? CloseTime,
    bool IsClosed);

public sealed record AgentDateSpecificHoursChange(
    string Action,
    DateOnly Date,
    TimeOnly? OpenTime,
    TimeOnly? CloseTime,
    bool? IsClosed);

/// <summary>Every field is optional; null leaves the stored value as is.</summary>
public sealed record AgentBrandingChange(
    string? DisplayName,
    string? Tagline,
    string? Announcement,
    string? PrimaryHexColor,
    string? SecondaryHexColor,
    string? LogoUrl,
    string? HeroImageUrl,
    string? PrimaryCtaUrl,
    string? Address,
    string? PhoneNumber,
    bool? HideHeroImage = null,
    bool? ShowTopContactSection = null,
    bool? ShowBottomContactSection = null,
    bool? ContactHoursWhiteBackground = null,
    int? ContactHoursWhiteBackgroundTransparency = null,
    bool? ApplyTransparencyToMenuItems = null,
    bool? ShowCustomBackground = null,
    string? CustomBackgroundMainHexColor = null,
    bool? CustomBackgroundMainColorActive = null,
    string? CustomBackgroundHexColor1 = null,
    string? CustomBackgroundHexColor2 = null,
    bool? CustomBackgroundSplash1Active = null,
    bool? CustomBackgroundSplash2Active = null,
    int? CustomBackgroundPosition1X = null,
    int? CustomBackgroundPosition1Y = null,
    int? CustomBackgroundPosition2X = null,
    int? CustomBackgroundPosition2Y = null,
    string? CustomBackgroundImageUrl = null,
    bool? CustomBackgroundImageActive = null);

/// <summary>Settings of the top-of-page and bottom-of-page hours sections; null leaves a setting as is.</summary>
public sealed record AgentHoursDisplayChange(
    bool? ShowTopHoursSection = null,
    string? TopHoursStyle = null,
    string? TopHoursOpenStatusDetail = null,
    bool? UseLegacyHoursStyle = null,
    bool? ShowBottomHoursSection = null,
    bool? UseLegacyBottomHoursStyle = null);

public sealed record AgentMenuSettingsChange(
    bool? AllowMenuPdfDownload = null);

/// <summary>
/// All fields are optional so the validator only needs to send what actually changed.
/// Unset (null) list fields mean "no changes of that kind".
/// </summary>
public sealed record AgentChangeSet(
    IReadOnlyList<AgentMenuCategoryChange>? MenuCategoryChanges,
    IReadOnlyList<AgentMenuItemChange>? MenuItemChanges,
    IReadOnlyList<AgentHoursChange>? HoursChanges,
    AgentBrandingChange? BrandingChange,
    IReadOnlyList<AgentDateSpecificHoursChange>? DateSpecificHoursChanges = null,
    IReadOnlyList<AgentMenuChange>? MenuChanges = null,
    AgentHoursDisplayChange? HoursDisplayChange = null,
    AgentMenuSettingsChange? MenuSettingsChange = null);

public sealed record ApplyAgentChangeSetRequest(
    Guid SubmissionId,
    double Confidence,
    string SourceChannel,
    AgentChangeSet ChangeSet);

/// <summary>
/// AuditId null means "the tenant's most recent change that can still be rolled back".
/// </summary>
public sealed record RollbackAgentChangeRequest(
    Guid SubmissionId,
    Guid? AuditId);

public sealed record CreateAgentSubmissionRequest(
    string Channel,
    string SenderIdentifier,
    string? RawBodyText,
    IReadOnlyList<string>? AttachmentRefs);

public sealed record UpdateAgentSubmissionStatusRequest(
    string Status,
    string? TranslatedJson,
    double? ValidatorConfidence,
    string? RejectionReason,
    string? ClarificationContext = null);

public sealed record ResolveTenantBySenderEmailRequest(
    string SenderEmail);
