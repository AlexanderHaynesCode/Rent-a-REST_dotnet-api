using System.Text.RegularExpressions;
using RentARestaurant.Api.Contracts;

namespace RentARestaurant.Api.Domain;

/// <summary>
/// Checks on a branding update shared by the admin portal and the agent pipeline, so the two can't
/// drift. Mirrors the restaurant_profiles CHECK constraints.
/// </summary>
public static class BrandingRules
{
    /// <summary>The call-to-action button's label; null shows <see cref="DefaultCtaText"/>.</summary>
    public const int MaxCtaTextLength = 30;
    public const int MaxCtaUrlLength = 2048;
    public const string DefaultCtaText = "Order Online";

    private static readonly Regex PhoneNumberPattern = new(@"^[0-9+\-().\s]+$", RegexOptions.Compiled);
    private static readonly Regex HexColorPattern = new(@"^#[0-9A-Fa-f]{6}([0-9A-Fa-f]{2})?$", RegexOptions.Compiled);
    // The admin portal used to store an uploaded image (R2 key tenants/{tenantId}/primary-cta) as the button's link.
    private static readonly Regex LegacyCtaImageUrlPattern = new(@"/tenants/[0-9A-Fa-f-]{36}/primary-cta", RegexOptions.Compiled);

    public static bool IsHexColor(string? value) => value is not null && HexColorPattern.IsMatch(value);

    /// <summary>
    /// Trims the call-to-action button's link and adds "https://" when the tenant typed it without a scheme
    /// ("order.example.com"). Blank means no link, which hides the button.
    /// </summary>
    public static string? NormalizeCtaUrl(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return trimmed.Contains("://", StringComparison.Ordinal) ? trimmed : $"https://{trimmed}";
    }

    /// <summary>
    /// Only absolute http(s) links, so the storefront never renders a javascript: or other unsafe href. A link with a
    /// user name or password ("mailto:a@b.com" becomes https://mailto:a@b.com) is never an ordering page either.
    /// </summary>
    public static bool IsValidCtaUrl(string? value) =>
        value is { Length: > 0 and <= MaxCtaUrlLength }
        && Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && !string.IsNullOrEmpty(uri.Host)
        && string.IsNullOrEmpty(uri.UserInfo);

    /// <summary>A stored link a rollback may put back: a valid web link that isn't one of the old uploaded CTA images.</summary>
    public static string? RestorableCtaUrl(string? value) =>
        IsValidCtaUrl(value) && !LegacyCtaImageUrlPattern.IsMatch(value!) ? value : null;

    /// <summary>Background glow positions are percentages chosen from an 11-step slider (0, 10, ..., 100).</summary>
    public static bool IsValidBackgroundPosition(int value) => value is >= 0 and <= 100 && value % 10 == 0;

    /// <summary>Returns what is wrong with an already-trimmed request, or null when it is valid.</summary>
    public static string? Validate(UpdateBrandingRequest request)
    {
        // Splash colors are kept while their toggles are off so re-enabling restores them, but each
        // is only required once the custom background is shown with that splash active.
        if (request.ShowCustomBackground &&
            ((request.CustomBackgroundSplash1Active && request.CustomBackgroundHexColor1 is null) ||
             (request.CustomBackgroundSplash2Active && request.CustomBackgroundHexColor2 is null)))
        {
            return "Each active background splash color needs a color when the custom background is enabled.";
        }

        // The main background color is optional: null means the site's default page color.
        if ((request.CustomBackgroundHexColor1 is not null && !IsHexColor(request.CustomBackgroundHexColor1)) ||
            (request.CustomBackgroundHexColor2 is not null && !IsHexColor(request.CustomBackgroundHexColor2)) ||
            (request.CustomBackgroundMainHexColor is not null && !IsHexColor(request.CustomBackgroundMainHexColor)))
        {
            return "Custom background colors must be hex colors like #1A2B3C.";
        }

        if (!IsValidBackgroundPosition(request.CustomBackgroundPosition1X) ||
            !IsValidBackgroundPosition(request.CustomBackgroundPosition1Y) ||
            !IsValidBackgroundPosition(request.CustomBackgroundPosition2X) ||
            !IsValidBackgroundPosition(request.CustomBackgroundPosition2Y))
        {
            return "Custom background positions must be between 0 and 100 in steps of 10.";
        }

        // Kept while the white background is off so re-enabling restores it.
        if (request.ContactHoursWhiteBackgroundTransparency is < ContactHoursWhiteBackgroundDefaults.MinTransparency
            or > ContactHoursWhiteBackgroundDefaults.MaxTransparency)
        {
            return "Contact and Hours white background transparency must be between 0 and 100.";
        }

        if (request.Address?.Length > 300)
        {
            return "Address must be 300 characters or fewer.";
        }

        if (request.PhoneNumber is not null &&
            (request.PhoneNumber.Length > 30 || !PhoneNumberPattern.IsMatch(request.PhoneNumber)))
        {
            return "Phone number must be 30 characters or fewer and contain only digits, spaces, and + - ( ) . characters.";
        }

        if (request.PrimaryCtaUrl is not null && !IsValidCtaUrl(request.PrimaryCtaUrl))
        {
            return "The call-to-action button link must be a web address like https://order.example.com.";
        }

        if (request.PrimaryCtaText?.Length > MaxCtaTextLength)
        {
            return $"The call-to-action button text must be {MaxCtaTextLength} characters or fewer.";
        }

        return null;
    }
}
