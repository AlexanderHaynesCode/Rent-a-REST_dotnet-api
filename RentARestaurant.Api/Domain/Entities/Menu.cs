using RentARestaurant.Api.Domain;

namespace RentARestaurant.Api.Domain.Entities;

/// <summary>
/// A named menu (e.g. "Lunch", "Dinner") that groups a tenant's menu categories. Every tenant has at
/// least one; existing tenants were backfilled with a default one named <see cref="DefaultName"/>.
/// </summary>
public class Menu : ITenantEntity
{
    public const string DefaultName = "Menu";

    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public bool IsVisible { get; set; } = true;

    public Tenant Tenant { get; set; } = null!;
    public ICollection<MenuCategory> Categories { get; set; } = new List<MenuCategory>();
}
