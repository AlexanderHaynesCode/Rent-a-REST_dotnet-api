using RentARestaurant.Api.Domain;

namespace RentARestaurant.Api.Domain.Entities;

public class RestaurantProfile : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Tagline { get; set; } = string.Empty;
    public string PrimaryHexColor { get; set; } = "#222222";
    public string SecondaryHexColor { get; set; } = "#ffffff";
    public string? LogoUrl { get; set; }
    public string? HeroImageUrl { get; set; }
    public string? PrimaryCtaUrl { get; set; }
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    public Tenant Tenant { get; set; } = null!;
}
