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

    /// <summary>
    /// Which of the background values below is drawn behind the menu: one of <see cref="MenuBackgroundRules.Styles"/>.
    /// The other values are kept while another style is chosen, so switching back restores them.
    /// </summary>
    public string BackgroundStyle { get; set; } = MenuBackgroundRules.NoneStyle;

    /// <summary>
    /// Only set by the admin menu background image upload/delete endpoints, an agent-applied email change, and
    /// clone/rollback.
    /// </summary>
    public string? BackgroundImageUrl { get; set; }

    public string? BackgroundHexColor { get; set; }

    public string? BackgroundPattern { get; set; }

    public bool ShowBackgroundOnSite { get; set; }

    public bool TextShadowEnabled { get; set; }

    public int TextShadowOffsetX { get; set; } = MenuTextShadowRules.DefaultOffsetX;
    public int TextShadowOffsetY { get; set; } = MenuTextShadowRules.DefaultOffsetY;
    public int TextShadowBlur { get; set; } = MenuTextShadowRules.DefaultBlur;
    public string TextShadowHexColor { get; set; } = MenuTextShadowRules.DefaultHexColor;
    public bool ShowTextShadowOnSite { get; set; }

    public Tenant Tenant { get; set; } = null!;
    public ICollection<MenuCategory> Categories { get; set; } = new List<MenuCategory>();
}
