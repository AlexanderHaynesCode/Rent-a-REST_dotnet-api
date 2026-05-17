namespace RentARestaurant.Api.Contracts;

public sealed record PublicRestaurantResponse(
    Guid TenantId,
    string Slug,
    string DisplayName,
    string Tagline,
    string PrimaryHexColor,
    string SecondaryHexColor,
    string? LogoUrl,
    string? HeroImageUrl,
    string? PrimaryCtaUrl,  // CTA = Call To Action button like "Order Now/Online"
    IReadOnlyList<PublicMenuCategoryResponse> Menu,
    IReadOnlyList<BusinessHourResponse> Hours);

public sealed record PublicMenuCategoryResponse(
    Guid Id,
    string Name,
    int SortOrder,
    IReadOnlyList<PublicMenuItemResponse> Items);

public sealed record PublicMenuItemResponse(
    Guid Id,
    string Name,
    string Description,
    decimal Price,
    bool IsAvailable,
    int SortOrder);

public sealed record BusinessHourResponse(
    DayOfWeek DayOfWeek,
    string OpenTime,
    string CloseTime,
    bool IsClosed);
