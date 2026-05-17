using RentARestaurant.Api.Domain;

namespace RentARestaurant.Api.Domain.Entities;

public class MenuCategory : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int SortOrder { get; set; }

    public Tenant Tenant { get; set; } = null!;
    public ICollection<MenuItem> Items { get; set; } = new List<MenuItem>();
}
