using RentARestaurant.Api.Domain;

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
    IReadOnlyList<DailyHourResponse>? DailyHours = null,
    IReadOnlyList<PublicMenuResponse>? Menus = null,
    bool AllowMenuPdfDownload = true,
    bool ShowTopContactSection = false,
    bool ShowBottomContactSection = true,
    bool ContactHoursWhiteBackground = false,
    int ContactHoursWhiteBackgroundTransparency = ContactHoursWhiteBackgroundDefaults.Transparency,
    bool ApplyTransparencyToMenuItems = false,
    bool HideHeroImage = false,
    string? PrimaryCtaText = null);

/// <summary>
/// The public storefront response only includes visible menus; the admin/agent snapshot includes all
/// of them so a rollback never drops categories that sit in a hidden menu.
/// The background and text shadow values are always filled in by the API. BackgroundStyle is only null in an
/// agent snapshot stored before menu backgrounds existed, which tells the rollback to leave the menu's background
/// alone; TextShadowEnabled does the same for snapshots stored before menu text shadows existed.
/// </summary>
public sealed record PublicMenuResponse(
    Guid Id,
    string Name,
    string Description,
    int SortOrder,
    bool IsVisible,
    string? BackgroundStyle = null,
    string? BackgroundImageUrl = null,
    string? BackgroundHexColor = null,
    string? BackgroundPattern = null,
    bool ShowBackgroundOnSite = false,
    bool? TextShadowEnabled = null,
    int TextShadowOffsetX = MenuTextShadowRules.DefaultOffsetX,
    int TextShadowOffsetY = MenuTextShadowRules.DefaultOffsetY,
    int TextShadowBlur = MenuTextShadowRules.DefaultBlur,
    string? TextShadowHexColor = MenuTextShadowRules.DefaultHexColor,
    bool ShowTextShadowOnSite = false)
{
    public static PublicMenuResponse From(Domain.Entities.Menu menu) => new(
        menu.Id,
        menu.Name,
        menu.Description,
        menu.SortOrder,
        menu.IsVisible,
        menu.BackgroundStyle,
        menu.BackgroundImageUrl,
        menu.BackgroundHexColor,
        menu.BackgroundPattern,
        menu.ShowBackgroundOnSite,
        menu.TextShadowEnabled,
        menu.TextShadowOffsetX,
        menu.TextShadowOffsetY,
        menu.TextShadowBlur,
        menu.TextShadowHexColor,
        menu.ShowTextShadowOnSite);
}

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
    IReadOnlyList<PublicMenuItemResponse> Items,
    Guid? MenuId = null);

public sealed record PublicMenuItemResponse(
    Guid Id,
    string Name,
    string Description,
    decimal Price,
    bool IsAvailable,
    int SortOrder,
    IReadOnlyList<MenuItemPriceOption>? PriceOptions = null)
{
    public static PublicMenuItemResponse From(Domain.Entities.MenuItem item) => new(
        item.Id,
        item.Name,
        item.Description,
        item.Price,
        item.IsAvailable,
        item.SortOrder,
        item.PriceOptions);
}

public sealed record BusinessHourResponse(
    DayOfWeek DayOfWeek,
    string OpenTime,
    string CloseTime,
    bool IsClosed);
