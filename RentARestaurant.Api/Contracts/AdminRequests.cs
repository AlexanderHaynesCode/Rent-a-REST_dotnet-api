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
    int CustomBackgroundPosition2Y = CustomBackgroundDefaults.Position2Y,
    string? CustomBackgroundMainHexColor = null,
    bool CustomBackgroundMainColorActive = true,
    bool CustomBackgroundSplash1Active = true,
    bool CustomBackgroundSplash2Active = true,
    // The page background image URL itself is managed only by the images/custom-background endpoints.
    bool CustomBackgroundImageActive = true,
    bool ShowTopContactSection = false,
    bool ShowBottomContactSection = true,
    bool ContactHoursWhiteBackground = false,
    int ContactHoursWhiteBackgroundTransparency = ContactHoursWhiteBackgroundDefaults.Transparency,
    bool HideHeroImage = false);

/// <summary>Range of the Contact/Hours white background's transparency (0 = solid white, 100 = see-through).</summary>
public static class ContactHoursWhiteBackgroundDefaults
{
    public const int Transparency = 0;
    public const int MinTransparency = 0;
    public const int MaxTransparency = 100;
}

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

public sealed record SetTopHoursSectionRequest(
    bool ShowTopHoursSection,
    string TopHoursStyle,
    string TopHoursOpenStatusDetail,
    bool UseLegacyHoursStyle);

/// <summary>Allowed values for the top-of-page hours section settings (mirrored by DB CHECK constraints).</summary>
public static class TopHoursOptions
{
    /// <summary>The full weekly hours list.</summary>
    public const string StandardStyle = "standard";
    /// <summary>An "Open Now!"/"Closed" message computed from the visitor's current time.</summary>
    public const string OpenStatusStyle = "open-status";

    public const string NoDetail = "none";
    public const string TodayDetail = "today";
    public const string TodayAndTomorrowDetail = "today-and-tomorrow";

    public static readonly IReadOnlySet<string> Styles = new HashSet<string> { StandardStyle, OpenStatusStyle };
    public static readonly IReadOnlySet<string> OpenStatusDetails = new HashSet<string> { NoDetail, TodayDetail, TodayAndTomorrowDetail };
}

public sealed record SetBottomHoursSectionRequest(
    bool ShowBottomHoursSection,
    bool UseLegacyBottomHoursStyle);

public sealed record UpsertDateSpecificHourRequest(
    DateOnly Date,
    TimeOnly OpenTime,
    TimeOnly CloseTime,
    bool IsClosed);

/// <summary>MenuId null means the tenant's default menu (the first by sort order).</summary>
public sealed record CreateMenuCategoryRequest(
    string Name,
    int SortOrder,
    string? Description = null,
    Guid? MenuId = null);

/// <summary>MenuId null keeps the category in its current menu.</summary>
public sealed record UpdateMenuCategoryRequest(
    string Name,
    string Description,
    int SortOrder,
    Guid? MenuId = null);

public sealed record CreateMenuRequest(
    string Name,
    int SortOrder,
    string? Description = null,
    bool IsVisible = true);

public sealed record UpdateMenuRequest(
    string Name,
    string Description,
    int SortOrder,
    bool IsVisible);

public sealed record SetMenuSettingsRequest(
    bool AllowMenuPdfDownload);

/// <summary>Per-tenant caps, enforced for both admin and agent writes.</summary>
public static class MenuLimits
{
    public const int MaxMenusPerTenant = 5;
    public const int MaxCategoriesPerTenant = 100;
    public const int MaxItemsPerTenant = 1000;
    public const int MaxNameLength = 120;
    public const int MaxDescriptionLength = 300;
}

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
