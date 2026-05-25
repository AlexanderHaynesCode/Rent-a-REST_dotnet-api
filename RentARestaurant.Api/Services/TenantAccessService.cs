using Microsoft.EntityFrameworkCore;
using RentARestaurant.Api.Data;

namespace RentARestaurant.Api.Services;

public class TenantAccessService(AppDbContext dbContext) : ITenantAccessService
{
    public Task<bool> CanManageTenantAsync(Guid tenantId, string externalUserId, CancellationToken cancellationToken)
    {
        return dbContext.TenantUsers.AnyAsync(
            x => x.TenantId == tenantId
                && x.ExternalUserId == externalUserId
                && (x.Role == "Owner" || x.Role == "Admin"),
            cancellationToken);
    }

    public async Task<TenantResolutionResult> ResolveSingleTenantForAdminAsync(string externalUserId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(externalUserId))
        {
            return new TenantResolutionResult(TenantResolutionStatus.NotFound, null);
        }

        var normalizedExternalUserId = externalUserId.Trim();

        var memberships = await dbContext.TenantUsers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(x => x.ExternalUserId == normalizedExternalUserId
                && (x.Role == "Owner" || x.Role == "Admin"))
            .Join(
                dbContext.Tenants.IgnoreQueryFilters().AsNoTracking(),
                user => user.TenantId,
                tenant => tenant.Id,
                (user, tenant) => new ResolvedTenantAccess(
                    tenant.Id,
                    tenant.Slug,
                    tenant.Name,
                    tenant.CustomDomain,
                    tenant.IsActive,
                    tenant.SubscriptionState))
            .ToListAsync(cancellationToken);

        if (memberships.Count == 0)
        {
            return new TenantResolutionResult(TenantResolutionStatus.NotFound, null);
        }

        if (memberships.Count > 1)
        {
            return new TenantResolutionResult(TenantResolutionStatus.MultipleMatches, null, memberships.Count);
        }

        var match = memberships[0];
        if (!match.IsActive)
        {
            return new TenantResolutionResult(TenantResolutionStatus.NotFound, null);
        }

        return new TenantResolutionResult(TenantResolutionStatus.Success, match, 1);
    }
}
