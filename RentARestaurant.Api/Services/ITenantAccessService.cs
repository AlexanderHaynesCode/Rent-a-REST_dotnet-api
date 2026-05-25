namespace RentARestaurant.Api.Services;

public enum TenantResolutionStatus
{
    Success,
    NotFound,
    MultipleMatches
}

public sealed record ResolvedTenantAccess(
    Guid TenantId,
    string Slug,
    string Name,
    string? CustomDomain,
    bool IsActive,
    string SubscriptionState);

public sealed record TenantResolutionResult(
    TenantResolutionStatus Status,
    ResolvedTenantAccess? Tenant,
    int MatchCount = 0);

public interface ITenantAccessService
{
    Task<bool> CanManageTenantAsync(Guid tenantId, string externalUserId, CancellationToken cancellationToken);

    Task<TenantResolutionResult> ResolveSingleTenantForAdminAsync(string externalUserId, CancellationToken cancellationToken);
}
