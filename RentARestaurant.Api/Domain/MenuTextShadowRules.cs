namespace RentARestaurant.Api.Domain;

/// <summary>
/// Defaults and allowed ranges for a menu's optional text shadow (CSS <c>text-shadow: {x}px {y}px {blur}px {color}</c>).
/// The admin portal draws it on the menu's text while it is on; the restaurant site only while the menu's
/// ShowTextShadowOnSite is on, too. The ranges are mirrored by CHECK constraints on <c>menus</c>, and by
/// admin-client-app (<c>domain/menuTextShadow.ts</c>) and restaurant-client-app (<c>utils/menuTextShadow.ts</c>).
/// </summary>
public static class MenuTextShadowRules
{
    public const int DefaultOffsetX = 2;
    public const int DefaultOffsetY = 2;
    public const int DefaultBlur = 0;
    public const string DefaultHexColor = "#FFFFFF";

    public const int MinOffset = -10;
    public const int MaxOffset = 10;
    public const int MinBlur = 0;
    public const int MaxBlur = 20;

    /// <summary>
    /// Returns what is wrong with an update's text shadow values, or null when they are valid. Each null value
    /// keeps the menu's current one.
    /// </summary>
    public static string? Validate(int? offsetX, int? offsetY, int? blur, string? hexColor)
    {
        if (offsetX is < MinOffset or > MaxOffset || offsetY is < MinOffset or > MaxOffset)
        {
            return $"Text shadow x and y must be between {MinOffset} and {MaxOffset} pixels.";
        }

        if (blur is < MinBlur or > MaxBlur)
        {
            return $"Text shadow blur must be between {MinBlur} and {MaxBlur} pixels.";
        }

        if (hexColor is not null && !BrandingRules.IsHexColor(hexColor))
        {
            return "Text shadow color must be a hex color like #1A2B3C.";
        }

        return null;
    }
}
