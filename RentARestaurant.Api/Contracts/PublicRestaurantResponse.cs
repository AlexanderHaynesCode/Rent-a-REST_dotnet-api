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
    string? Address,
    string? PhoneNumber,
    IReadOnlyList<PublicMenuCategoryResponse> Menu,
    IReadOnlyList<BusinessHourResponse> Hours,
    IReadOnlyList<DateSpecificHourSnapshot>? DateSpecificHours = null,
    bool UseLegacyHoursStyle = false,
    bool ShowBottomHoursSection = true,
    bool UseLegacyBottomHoursStyle = false,
    bool ShowCustomBackground = false,
    string? CustomBackgroundHexColor1 = null,
    string? CustomBackgroundHexColor2 = null,
    int CustomBackgroundPosition1X = CustomBackgroundDefaults.Position1X,
    int CustomBackgroundPosition1Y = CustomBackgroundDefaults.Position1Y,
    int CustomBackgroundPosition2X = CustomBackgroundDefaults.Position2X,
    int CustomBackgroundPosition2Y = CustomBackgroundDefaults.Position2Y,
    string? CustomBackgroundMainHexColor = null,
    bool CustomBackgroundMainColorActive = true,
    bool CustomBackgroundSplash1Active = true,
    bool CustomBackgroundSplash2Active = true,
    string? CustomBackgroundImageUrl = null,
    bool CustomBackgroundImageActive = true,
    bool ShowTopHoursSection = true,
    string TopHoursStyle = TopHoursOptions.StandardStyle,
    string TopHoursOpenStatusDetail = TopHoursOptions.NoDetail,
    IReadOnlyList<DailyHourResponse>? DailyHours = null);

/// <summary>
/// The hours that apply on one calendar date: that date's date-specific override, or else the
/// weekday's regular hours. The storefront uses it for the "Open Now!"/"Closed" check, including
/// the previous day's hours running past midnight.
/// </summary>
public sealed record DailyHourResponse(
    DateOnly Date,
    DayOfWeek DayOfWeek,
    string OpenTime,
    string CloseTime,
    bool IsClosed);

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
