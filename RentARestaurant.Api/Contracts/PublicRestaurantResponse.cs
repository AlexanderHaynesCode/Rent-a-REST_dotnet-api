namespace RentARestaurant.Api.Contracts;

public sealed record PublicRestaurantResponse(
    Guid TenantId,
    string Slug,
    string DisplayName,
    string Tagline,
    string Announcement,
    string PrimaryHexColor,
    string SecondaryHexColor,
    string? LogoUrl,
    string? HeroImageUrl,
    string? PrimaryCtaUrl,  // CTA = Call To Action button like "Order Now/Online"
    IReadOnlyList<PublicMenuCategoryResponse> Menu,
    IReadOnlyList<BusinessHourResponse> Hours,
    IReadOnlyList<DateSpecificHourSnapshot>? DateSpecificHours = null);

/// <summary>Only populated by the admin/agent snapshot; the public storefront response leaves it null.</summary>
public sealed record DateSpecificHourSnapshot(
    DateOnly Date,
    string OpenTime,
    string CloseTime,
    bool IsClosed);

public sealed record PublicMenuCategoryResponse(
    Guid Id,
    string Name,
    string Description,
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
