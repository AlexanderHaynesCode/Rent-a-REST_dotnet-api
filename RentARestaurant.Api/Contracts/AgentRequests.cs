namespace RentARestaurant.Api.Contracts;

public enum AgentItemAction
{
    Create,
    Update,
    Delete
}

public sealed record AgentMenuCategoryChange(
    string Action,
    Guid? CategoryId,
    string? Name,
    int? SortOrder);

public sealed record AgentMenuItemChange(
    string Action,
    Guid? ItemId,
    Guid? CategoryId,
    string? CategoryName,
    string? Name,
    string? Description,
    decimal? Price,
    bool? IsAvailable,
    int? SortOrder);

public sealed record AgentHoursChange(
    DayOfWeek DayOfWeek,
    TimeOnly OpenTime,
    TimeOnly CloseTime,
    bool IsClosed);

public sealed record AgentBrandingChange(
    string? DisplayName,
    string? Tagline,
    string? PrimaryHexColor,
    string? SecondaryHexColor,
    string? LogoUrl,
    string? HeroImageUrl,
    string? PrimaryCtaUrl);

/// <summary>
/// All fields are optional so the validator only needs to send what actually changed.
/// Unset (null) list fields mean "no changes of that kind".
/// </summary>
public sealed record AgentChangeSet(
    IReadOnlyList<AgentMenuCategoryChange>? MenuCategoryChanges,
    IReadOnlyList<AgentMenuItemChange>? MenuItemChanges,
    IReadOnlyList<AgentHoursChange>? HoursChanges,
    AgentBrandingChange? BrandingChange);

public sealed record ApplyAgentChangeSetRequest(
    Guid SubmissionId,
    double Confidence,
    string SourceChannel,
    AgentChangeSet ChangeSet);

public sealed record CreateAgentSubmissionRequest(
    string Channel,
    string SenderIdentifier,
    string? RawBodyText,
    IReadOnlyList<string>? AttachmentRefs);

public sealed record UpdateAgentSubmissionStatusRequest(
    string Status,
    string? TranslatedJson,
    double? ValidatorConfidence,
    string? RejectionReason);

public sealed record ResolveTenantBySenderEmailRequest(
    string SenderEmail);
