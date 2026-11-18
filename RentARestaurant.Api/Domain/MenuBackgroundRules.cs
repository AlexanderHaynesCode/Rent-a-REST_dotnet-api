namespace RentARestaurant.Api.Domain;

/// <summary>
/// Allowed values for a menu's optional background. The admin portal always draws it behind the menu's row; the
/// restaurant site only draws it while the menu's ShowBackgroundOnSite is on. Styles are mirrored by the
/// <c>menus_background_style_values</c> CHECK constraint. Patterns are only checked here, so adding one needs no
/// DB change; admin-client-app (<c>domain/menuBackgrounds.ts</c>) and restaurant-client-app
/// (<c>utils/menuBackground.ts</c>) each draw them with the same CSS.
/// </summary>
public static class MenuBackgroundRules
{
    public const string NoneStyle = "none";
    public const string ImageStyle = "image";
    public const string ColorStyle = "color";
    public const string PatternStyle = "pattern";

    public static readonly IReadOnlyList<string> Styles = [NoneStyle, ImageStyle, ColorStyle, PatternStyle];

    public static readonly IReadOnlyList<string> Patterns =
    [
        "gingham", "subway-tile", "terrazzo", "parchment", "linen-paper", "watercolor-paper", "handmade-paper",
    ];

    private const string ImageTypePrefix = "menu-background-";

    /// <summary>
    /// Returns what is wrong with an update's background fields, or null when they are valid. Each null value
    /// keeps the menu's current one.
    /// </summary>
    public static string? Validate(string? style, string? hexColor, string? pattern)
    {
        if (style is not null && !Styles.Contains(style))
        {
            return $"Menu background must be one of: {string.Join(", ", Styles)}.";
        }

        if (hexColor is not null && !BrandingRules.IsHexColor(hexColor))
        {
            return "Menu background color must be a hex color like #1A2B3C.";
        }

        if (pattern is not null && !Patterns.Contains(pattern))
        {
            return $"Menu background pattern must be one of: {string.Join(", ", Patterns)}.";
        }

        return null;
    }

    /// <summary>
    /// The R2 image type (the last part of the key <c>tenants/{tenantId}/{imageType}</c>) for a new upload. Unlike
    /// the branding images, every upload gets its own key: a cloned menu shares its source's image URL, so
    /// replacing the source's image must not change the copy's.
    /// </summary>
    public static string NewImageType(Guid menuId) =>
        $"{ImageTypePrefix}{menuId:N}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";

    /// <summary>
    /// The image type of a stored menu background URL, or null when the URL isn't a menu background image in
    /// this tenant's folder (so nothing else can ever be deleted through it).
    /// </summary>
    public static string? ImageTypeFromUrl(Guid tenantId, string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var folder = $"/tenants/{tenantId:D}/";
        var path = uri.AbsolutePath;
        var folderIndex = path.LastIndexOf(folder, StringComparison.OrdinalIgnoreCase);
        if (folderIndex < 0)
        {
            return null;
        }

        var imageType = path[(folderIndex + folder.Length)..];
        return imageType.StartsWith(ImageTypePrefix, StringComparison.Ordinal) && !imageType.Contains('/')
            ? imageType
            : null;
    }
}
