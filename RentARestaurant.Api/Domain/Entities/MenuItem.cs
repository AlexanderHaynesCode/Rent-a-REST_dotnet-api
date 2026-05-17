using RentARestaurant.Api.Domain;

namespace RentARestaurant.Api.Domain.Entities;

public class MenuItem : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CategoryId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public bool IsAvailable { get; set; } = true;
    public int SortOrder { get; set; }

    public Tenant Tenant { get; set; } = null!;
    public MenuCategory Category { get; set; } = null!;
}
