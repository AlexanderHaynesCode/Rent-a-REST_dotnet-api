namespace RentARestaurant.Api.Contracts;

public sealed record AgentSnapshotResponse(
    PublicRestaurantResponse Restaurant,
    bool AutoApplyEligible);

public sealed record AgentApplyResponse(
    Guid AuditId,
    DateTime AppliedUtc,
    DateTime RollbackExpiresUtc,
    IReadOnlyList<string> AppliedSummary);

public sealed record AgentSubmissionResponse(
    Guid Id,
    Guid TenantId,
    string Channel,
    string Status,
    DateTime ReceivedUtc);

public sealed record AgentPendingClarificationResponse(
    Guid Id,
    string? TranslatedJson,
    string? RejectionReason,
    DateTime ReceivedUtc);

public sealed record ResolveTenantBySenderEmailResponse(
    string Status,
    int MatchCount,
    Guid? TenantId,
    string? TenantSlug,
    string? TenantName,
    string? SubscriptionPlan,
    bool AutoApplyEligible);
