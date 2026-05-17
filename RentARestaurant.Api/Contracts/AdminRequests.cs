namespace RentARestaurant.Api.Contracts;

public sealed record UpdateBrandingRequest(
    string DisplayName,
    string Tagline,
    string PrimaryHexColor,
    string SecondaryHexColor,
    string? LogoUrl,
    string? HeroImageUrl,
    string? PrimaryCtaUrl);

public sealed record UpsertBusinessHourRequest(
    DayOfWeek DayOfWeek,
    TimeOnly OpenTime,
    TimeOnly CloseTime,
    bool IsClosed);

public sealed record CreateMenuCategoryRequest(
    string Name,
    int SortOrder);

public sealed record CreateMenuItemRequest(
    Guid CategoryId,
    string Name,
    string Description,
    decimal Price,
    bool IsAvailable,
    int SortOrder);
