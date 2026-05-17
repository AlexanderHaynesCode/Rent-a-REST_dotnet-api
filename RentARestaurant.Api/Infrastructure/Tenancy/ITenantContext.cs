namespace RentARestaurant.Api.Infrastructure.Tenancy;

public interface ITenantContext
{
    Guid? TenantId { get; }
    string? TenantSlug { get; }
    bool IsSystemRequest { get; }

    void SetTenant(Guid tenantId, string slug);
    void MarkSystemRequest();
}
