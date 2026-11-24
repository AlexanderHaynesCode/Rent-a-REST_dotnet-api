using RentARestaurant.Api.Contracts;
using RentARestaurant.Api.Domain;
using RentARestaurant.Api.Domain.Entities;

namespace RentARestaurant.Api.Services;

/// <summary>
/// Turns the difference between a restaurant profile before and after an agent change into the
/// plain-language lines shown in the tenant's "updates are live" email, using the admin portal's wording.
/// </summary>
public static class ProfileChangeSummary
{
    private const int MaxFileNameLength = 120;

    public static List<string> Describe(
        RestaurantProfile before,
        RestaurantProfile after,
        IReadOnlyDictionary<string, string>? imageFileNames = null)
    {
        var lines = new List<string>();

        void Text(string? oldValue, string? newValue, string label)
        {
            if (Same(oldValue, newValue)) return;
            lines.Add(string.IsNullOrWhiteSpace(newValue) ? $"Cleared the {label}." : $"Changed the {label} to \"{newValue}\".");
        }

        void Image(string? oldValue, string? newValue, string label)
        {
            if (Same(oldValue, newValue)) return;
            if (string.IsNullOrWhiteSpace(newValue))
            {
                lines.Add($"Removed the {label}.");
                return;
            }

            var fileName = AttachedFileName(imageFileNames, newValue);
            lines.Add(fileName is null ? $"Updated the {label}." : $"Updated the {label} to \"{fileName}\".");
        }

        void Toggle(bool oldValue, bool newValue, string whenOn, string whenOff)
        {
            if (oldValue != newValue) lines.Add(newValue ? whenOn : whenOff);
        }

        Text(before.DisplayName, after.DisplayName, "display name");
        Text(before.Tagline, after.Tagline, "tagline");
        Text(before.Announcement, after.Announcement, "announcement");
        Text(before.PrimaryHexColor, after.PrimaryHexColor, "primary color");
        Text(before.SecondaryHexColor, after.SecondaryHexColor, "secondary color");
        Image(before.LogoUrl, after.LogoUrl, "logo");
        Image(before.HeroImageUrl, after.HeroImageUrl, "hero image");
        if (!Same(before.PrimaryCtaUrl, after.PrimaryCtaUrl))
        {
            lines.Add(string.IsNullOrWhiteSpace(after.PrimaryCtaUrl)
                ? "Removed the call-to-action button."
                : $"Changed the call-to-action button link to \"{after.PrimaryCtaUrl}\".");
        }
        if (!Same(before.PrimaryCtaText, after.PrimaryCtaText))
        {
            lines.Add(string.IsNullOrWhiteSpace(after.PrimaryCtaText)
                ? $"Reset the call-to-action button text to \"{BrandingRules.DefaultCtaText}\"."
                : $"Changed the call-to-action button text to \"{after.PrimaryCtaText}\".");
        }
        Text(before.Address, after.Address, "address");
        Text(before.PhoneNumber, after.PhoneNumber, "phone number");

        Toggle(before.HideHeroImage, after.HideHeroImage, "Hid the hero image.", "Showing the hero image again.");
        Toggle(before.ShowTopContactSection, after.ShowTopContactSection,
            "Showing the top-of-page Contact section.", "Hid the top-of-page Contact section.");
        Toggle(before.ShowBottomContactSection, after.ShowBottomContactSection,
            "Showing the bottom-of-page Contact section.", "Hid the bottom-of-page Contact section.");

        Toggle(before.ContactHoursWhiteBackground, after.ContactHoursWhiteBackground,
            "Added a white background behind the Contact and Hours sections.",
            "Removed the white background behind the Contact and Hours sections.");
        if (before.ContactHoursWhiteBackgroundTransparency != after.ContactHoursWhiteBackgroundTransparency)
        {
            lines.Add($"Set the white background transparency to {after.ContactHoursWhiteBackgroundTransparency}%.");
        }
        Toggle(before.ApplyTransparencyToMenuItems, after.ApplyTransparencyToMenuItems,
            "Applied the transparency setting to menu items.", "Stopped applying the transparency setting to menu items.");

        Toggle(before.ShowCustomBackground, after.ShowCustomBackground,
            "Turned on the custom background.", "Turned off the custom background.");
        Text(before.CustomBackgroundMainHexColor, after.CustomBackgroundMainHexColor, "main background color");
        Toggle(before.CustomBackgroundMainColorActive, after.CustomBackgroundMainColorActive,
            "Turned on the main background color.", "Turned off the main background color.");
        Text(before.CustomBackgroundHexColor1, after.CustomBackgroundHexColor1, "background splash color 1");
        Toggle(before.CustomBackgroundSplash1Active, after.CustomBackgroundSplash1Active,
            "Turned on background splash color 1.", "Turned off background splash color 1.");
        if (before.CustomBackgroundPosition1X != after.CustomBackgroundPosition1X ||
            before.CustomBackgroundPosition1Y != after.CustomBackgroundPosition1Y)
        {
            lines.Add("Moved background splash color 1.");
        }
        Text(before.CustomBackgroundHexColor2, after.CustomBackgroundHexColor2, "background splash color 2");
        Toggle(before.CustomBackgroundSplash2Active, after.CustomBackgroundSplash2Active,
            "Turned on background splash color 2.", "Turned off background splash color 2.");
        if (before.CustomBackgroundPosition2X != after.CustomBackgroundPosition2X ||
            before.CustomBackgroundPosition2Y != after.CustomBackgroundPosition2Y)
        {
            lines.Add("Moved background splash color 2.");
        }
        Image(before.CustomBackgroundImageUrl, after.CustomBackgroundImageUrl, "page background image");
        Toggle(before.CustomBackgroundImageActive, after.CustomBackgroundImageActive,
            "Turned on the page background image.", "Turned off the page background image.");

        Toggle(before.ShowTopHoursSection, after.ShowTopHoursSection,
            "Showing the top-of-page hours section.", "Hid the top-of-page hours section.");
        if (before.TopHoursStyle != after.TopHoursStyle)
        {
            lines.Add(after.TopHoursStyle == TopHoursOptions.OpenStatusStyle
                ? "Switched the top-of-page hours to the \"Open Now!/Closed\" styling."
                : "Switched the top-of-page hours to the standard hours styling.");
        }
        if (before.TopHoursOpenStatusDetail != after.TopHoursOpenStatusDetail)
        {
            lines.Add(after.TopHoursOpenStatusDetail switch
            {
                TopHoursOptions.TodayDetail => "The top-of-page \"Open Now!/Closed\" message now shows today's hours.",
                TopHoursOptions.TodayAndTomorrowDetail => "The top-of-page \"Open Now!/Closed\" message now shows today's and tomorrow's hours.",
                _ => "The top-of-page \"Open Now!/Closed\" message no longer lists hours."
            });
        }
        Toggle(before.UseLegacyHoursStyle, after.UseLegacyHoursStyle,
            "Turned on legacy styling for the top-of-page hours.", "Turned off legacy styling for the top-of-page hours.");
        Toggle(before.ShowBottomHoursSection, after.ShowBottomHoursSection,
            "Showing the bottom-of-page hours section.", "Hid the bottom-of-page hours section.");
        Toggle(before.UseLegacyBottomHoursStyle, after.UseLegacyBottomHoursStyle,
            "Turned on legacy styling for the bottom-of-page hours.", "Turned off legacy styling for the bottom-of-page hours.");

        Toggle(before.AllowMenuPdfDownload, after.AllowMenuPdfDownload,
            "Customers can now download your menu as a PDF.", "Turned off menu PDF downloads.");

        return lines;
    }

    /// <summary>The name of the attached file an image URL was uploaded from, or null when it isn't known.</summary>
    public static string? AttachedFileName(IReadOnlyDictionary<string, string>? imageFileNames, string? url)
    {
        if (imageFileNames is null || string.IsNullOrWhiteSpace(url)
            || !imageFileNames.TryGetValue(url.Trim(), out var name) || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        name = name.Trim();
        return name.Length > MaxFileNameLength ? name[..MaxFileNameLength] + "..." : name;
    }

    private static bool Same(string? left, string? right) =>
        string.Equals(
            string.IsNullOrWhiteSpace(left) ? null : left,
            string.IsNullOrWhiteSpace(right) ? null : right,
            StringComparison.Ordinal);
}
