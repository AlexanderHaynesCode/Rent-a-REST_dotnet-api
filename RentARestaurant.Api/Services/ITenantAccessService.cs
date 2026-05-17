namespace RentARestaurant.Api.Services;

public interface ITenantAccessService
{
    Task<bool> CanManageTenantAsync(Guid tenantId, string externalUserId, CancellationToken cancellationToken);
}
