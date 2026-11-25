using RentARestaurant.Api.Domain;

namespace RentARestaurant.Api.Domain.Entities;

public class MenuItem : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CategoryId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    /// <summary>The item's price, or the first size's price while it has sizes.</summary>
    public decimal Price { get; set; }
    /// <summary>Null for a single price; otherwise 2-4 sizes (see <see cref="MenuItemPriceRules"/>).</summary>
    public IReadOnlyList<MenuItemPriceOption>? PriceOptions { get; set; }
    public bool IsAvailable { get; set; } = true;
    public int SortOrder { get; set; }

    public Tenant Tenant { get; set; } = null!;
    public MenuCategory Category { get; set; } = null!;
}
