using RentARestaurant.Api.Domain;

namespace RentARestaurant.Api.Domain.Entities;

public class RestaurantProfile : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Tagline { get; set; } = string.Empty;
    public string Announcement { get; set; } = string.Empty;
    public string PrimaryHexColor { get; set; } = "#222222";
    public string SecondaryHexColor { get; set; } = "#ffffff";
    public string? LogoUrl { get; set; }
    public string? HeroImageUrl { get; set; }
    public string? PrimaryCtaUrl { get; set; }
    public string? Address { get; set; }
    public string? PhoneNumber { get; set; }
    public bool UseLegacyHoursStyle { get; set; }
    public bool ShowBottomHoursSection { get; set; } = true;
    public bool UseLegacyBottomHoursStyle { get; set; }
    public bool ShowCustomBackground { get; set; }
    public string? CustomBackgroundHexColor1 { get; set; }
    public string? CustomBackgroundHexColor2 { get; set; }
    public int CustomBackgroundPosition1X { get; set; } = Contracts.CustomBackgroundDefaults.Position1X;
    public int CustomBackgroundPosition1Y { get; set; } = Contracts.CustomBackgroundDefaults.Position1Y;
    public int CustomBackgroundPosition2X { get; set; } = Contracts.CustomBackgroundDefaults.Position2X;
    public int CustomBackgroundPosition2Y { get; set; } = Contracts.CustomBackgroundDefaults.Position2Y;
    public string? CustomBackgroundMainHexColor { get; set; }
    public bool CustomBackgroundMainColorActive { get; set; } = true;
    public bool CustomBackgroundSplash1Active { get; set; } = true;
    public bool CustomBackgroundSplash2Active { get; set; } = true;
    /// <summary>Page background image shown below the hero. Only set by the custom-background image endpoints.</summary>
    public string? CustomBackgroundImageUrl { get; set; }
    public bool CustomBackgroundImageActive { get; set; } = true;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    public Tenant Tenant { get; set; } = null!;
}
