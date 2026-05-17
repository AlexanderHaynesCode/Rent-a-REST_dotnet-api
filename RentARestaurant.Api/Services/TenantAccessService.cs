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
}
