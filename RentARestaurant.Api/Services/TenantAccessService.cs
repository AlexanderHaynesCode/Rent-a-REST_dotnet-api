using Microsoft.EntityFrameworkCore;
using RentARestaurant.Api.Contracts;
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

    public async Task<SenderTenantResolutionResult> ResolveTenantBySenderEmailAsync(string senderEmail, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(senderEmail))
        {
            return new SenderTenantResolutionResult(SenderTenantResolutionStatus.NotFound, null);
        }

        var normalizedEmail = senderEmail.Trim().ToLowerInvariant();

        // Join+GroupBy over a record projection isn't SQL-translatable, so project to an
        // anonymous type in the query and do the grouping/dedupe client-side after materializing.
        var rows = await dbContext.TenantUsers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(x => x.Email.ToLower() == normalizedEmail)
            .Join(
                dbContext.Tenants.IgnoreQueryFilters().AsNoTracking(),
                user => user.TenantId,
                tenant => tenant.Id,
                (user, tenant) => new
                {
                    tenant.Id,
                    tenant.Slug,
                    tenant.Name,
                    tenant.SubscriptionPlan,
                    tenant.IsActive
                })
            .Where(x => x.IsActive)
            .ToListAsync(cancellationToken);

        var memberships = rows
            .GroupBy(x => x.Id)
            .Select(group => group.First())
            .Select(x => new ResolvedSenderTenant(x.Id, x.Slug, x.Name, x.SubscriptionPlan, x.IsActive))
            .ToList();

        if (memberships.Count == 0)
        {
            return new SenderTenantResolutionResult(SenderTenantResolutionStatus.NotFound, null);
        }

        if (memberships.Count > 1)
        {
            return new SenderTenantResolutionResult(SenderTenantResolutionStatus.MultipleMatches, null, memberships.Count);
        }

        var tenant = memberships[0];
        if (!string.Equals(tenant.SubscriptionPlan, SubscriptionPlans.DoneForYou, StringComparison.Ordinal))
        {
            return new SenderTenantResolutionResult(SenderTenantResolutionStatus.NotEligible, tenant, 1);
        }

        return new SenderTenantResolutionResult(SenderTenantResolutionStatus.Success, tenant, 1);
    }
}
