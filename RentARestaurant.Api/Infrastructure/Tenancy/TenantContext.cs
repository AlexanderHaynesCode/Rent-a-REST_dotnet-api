namespace RentARestaurant.Api.Infrastructure.Tenancy;

public class TenantContext : ITenantContext
{
    public Guid? TenantId { get; private set; }
    public string? TenantSlug { get; private set; }
    public bool IsSystemRequest { get; private set; }

    public void SetTenant(Guid tenantId, string slug)
    {
        TenantId = tenantId;
        TenantSlug = slug;
    }

    public void MarkSystemRequest()
    {
        IsSystemRequest = true;
    }
}
