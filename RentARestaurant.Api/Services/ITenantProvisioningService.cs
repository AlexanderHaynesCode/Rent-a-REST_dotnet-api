using RentARestaurant.Api.Contracts;

namespace RentARestaurant.Api.Services;

public interface ITenantProvisioningService
{
    Task<ProvisionTenantResponse> ProvisionTenantAsync(ProvisionTenantRequest request, CancellationToken cancellationToken);
}
