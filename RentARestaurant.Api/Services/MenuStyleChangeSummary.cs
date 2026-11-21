using RentARestaurant.Api.Domain;
using RentARestaurant.Api.Domain.Entities;

namespace RentARestaurant.Api.Services;

/// <summary>
/// Turns the difference in a menu's background and text shadow before and after an agent change into the
/// plain-language lines shown in the tenant's "updates are live" email, using the admin portal's wording.
/// </summary>
public static class MenuStyleChangeSummary
{
    public static List<string> Describe(Menu before, Menu after)
    {
        var lines = new List<string>();
        var menu = $"the menu '{after.Name}'";

        var styleBefore = before.BackgroundStyle;
        var styleAfter = after.BackgroundStyle;
        if (styleAfter == MenuBackgroundRules.NoneStyle)
        {
            if (styleBefore != MenuBackgroundRules.NoneStyle)
            {
                lines.Add($"Removed the background from {menu}.");
            }
        }
        else if (styleBefore == MenuBackgroundRules.NoneStyle)
        {
            lines.Add($"Added {BackgroundDescription(after)} to {menu}.");
        }
        else if (styleBefore != styleAfter)
        {
            lines.Add($"Changed the background of {menu} to {BackgroundDescription(after)}.");
        }
        else if (styleAfter == MenuBackgroundRules.ColorStyle && !SameText(before.BackgroundHexColor, after.BackgroundHexColor))
        {
            lines.Add($"Changed the background color of {menu} to {after.BackgroundHexColor}.");
        }
        else if (styleAfter == MenuBackgroundRules.PatternStyle && before.BackgroundPattern != after.BackgroundPattern)
        {
            lines.Add($"Changed the background of {menu} to the {MenuBackgroundRules.PatternLabel(after.BackgroundPattern)} pattern.");
        }
        else if (styleAfter == MenuBackgroundRules.ImageStyle && before.BackgroundImageUrl != after.BackgroundImageUrl)
        {
            lines.Add(string.IsNullOrWhiteSpace(after.BackgroundImageUrl)
                ? $"Removed the background image of {menu}."
                : $"Updated the background image of {menu}.");
        }

        if (before.ShowBackgroundOnSite != after.ShowBackgroundOnSite)
        {
            lines.Add(after.ShowBackgroundOnSite
                ? $"The background of {menu} now shows on your site."
                : $"The background of {menu} now only shows in your admin portal.");
        }

        if (before.TextShadowEnabled != after.TextShadowEnabled)
        {
            lines.Add(after.TextShadowEnabled
                ? $"Added a text shadow to {menu} ({TextShadowDescription(after)})."
                : $"Removed the text shadow from {menu}.");
        }
        else if (after.TextShadowEnabled && !SameText(TextShadowDescription(before), TextShadowDescription(after)))
        {
            lines.Add($"Changed the text shadow of {menu} to {TextShadowDescription(after)}.");
        }

        if (before.ShowTextShadowOnSite != after.ShowTextShadowOnSite)
        {
            lines.Add(after.ShowTextShadowOnSite
                ? $"The text shadow of {menu} now shows on your site."
                : $"The text shadow of {menu} now only shows in your admin portal.");
        }

        return lines;
    }

    private static string BackgroundDescription(Menu menu) => menu.BackgroundStyle switch
    {
        MenuBackgroundRules.ColorStyle => $"a solid color background ({menu.BackgroundHexColor})",
        MenuBackgroundRules.PatternStyle => $"a {MenuBackgroundRules.PatternLabel(menu.BackgroundPattern)} pattern background",
        MenuBackgroundRules.ImageStyle => "an image background",
        _ => "a background"
    };

    private static string TextShadowDescription(Menu menu) =>
        $"x {menu.TextShadowOffsetX}px, y {menu.TextShadowOffsetY}px, blur {menu.TextShadowBlur}px, {menu.TextShadowHexColor}";

    private static bool SameText(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
