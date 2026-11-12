namespace RentARestaurant.Api.Contracts;

public sealed record UpdateBrandingRequest(
    string DisplayName,
    string Tagline,
    string Announcement,
    string PrimaryHexColor,
    string SecondaryHexColor,
    string? LogoUrl,
    string? HeroImageUrl,
    string? PrimaryCtaUrl,
    string? Address = null,
    string? PhoneNumber = null,
    bool ShowCustomBackground = false,
    string? CustomBackgroundHexColor1 = null,
    string? CustomBackgroundHexColor2 = null,
    int CustomBackgroundPosition1X = CustomBackgroundDefaults.Position1X,
    int CustomBackgroundPosition1Y = CustomBackgroundDefaults.Position1Y,
    int CustomBackgroundPosition2X = CustomBackgroundDefaults.Position2X,
    int CustomBackgroundPosition2Y = CustomBackgroundDefaults.Position2Y);

/// <summary>Default percentage positions of the two custom background glow circles ("circle at X% Y%").</summary>
public static class CustomBackgroundDefaults
{
    public const int Position1X = 20;
    public const int Position1Y = 10;
    public const int Position2X = 80;
    public const int Position2Y = 10;
}

public sealed record UpsertBusinessHourRequest(
    DayOfWeek DayOfWeek,
    TimeOnly OpenTime,
    TimeOnly CloseTime,
    bool IsClosed);

public sealed record SetHoursStyleRequest(bool UseLegacyHoursStyle);

public sealed record SetBottomHoursSectionRequest(
    bool ShowBottomHoursSection,
    bool UseLegacyBottomHoursStyle);

public sealed record UpsertDateSpecificHourRequest(
    DateOnly Date,
    TimeOnly OpenTime,
    TimeOnly CloseTime,
    bool IsClosed);

public sealed record CreateMenuCategoryRequest(
    string Name,
    int SortOrder,
    string? Description = null);

public sealed record UpdateMenuCategoryRequest(
    string Name,
    string Description,
    int SortOrder);

public sealed record CreateMenuItemRequest(
    Guid CategoryId,
    string Name,
    string Description,
    decimal Price,
    bool IsAvailable,
    int SortOrder);

public sealed record UpdateMenuItemRequest(
    Guid CategoryId,
    string Name,
    string Description,
    decimal Price,
    bool IsAvailable,
    int SortOrder);

public sealed record MenuReorderEntry(
    Guid Id,
    int SortOrder);
