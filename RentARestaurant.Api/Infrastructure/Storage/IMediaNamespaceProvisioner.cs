namespace RentARestaurant.Api.Infrastructure.Storage;

public interface IMediaNamespaceProvisioner
{
    Task<string> CreateTenantNamespaceAsync(Guid tenantId, CancellationToken cancellationToken);
}
