using RentARestaurant.Api.Contracts;

namespace RentARestaurant.Api.Domain;

/// <summary>
/// Naming and limit rules for copying a menu with all of its categories and items into a new menu.
/// admin-client-app mirrors these in <c>domain/menus.ts</c> (suggestCloneName, cloneLimitProblems) so it can
/// suggest the same name and refuse the same clones before calling the API.
/// </summary>
public static class MenuCloneRules
{
    public const string CopySuffix = "_copy";

    /// <summary>
    /// "Lunch_copy", or "Lunch_copy_2", "Lunch_copy_3"... when that name is taken (ignoring case). The source
    /// name is shortened when needed so the result fits <see cref="MenuLimits.MaxNameLength"/>.
    /// </summary>
    public static string NextCopyName(string sourceName, IEnumerable<string> existingNames)
    {
        var taken = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);
        for (var number = 1; ; number++)
        {
            var suffix = number == 1 ? CopySuffix : $"{CopySuffix}_{number}";
            var baseName = sourceName.Length + suffix.Length > MenuLimits.MaxNameLength
                ? sourceName[..(MenuLimits.MaxNameLength - suffix.Length)].TrimEnd()
                : sourceName;
            var candidate = baseName + suffix;
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>
    /// Tenant-facing reasons the copy would take the tenant past a menu, category or item limit; empty when it fits.
    /// The counts are the tenant's current totals across all menus and the size of the menu being copied.
    /// </summary>
    public static IReadOnlyList<string> LimitProblems(
        int menuCount,
        int categoryCount,
        int itemCount,
        int sourceCategoryCount,
        int sourceItemCount)
    {
        var problems = new List<string>();
        if (menuCount >= MenuLimits.MaxMenusPerTenant)
        {
            problems.Add($"You already have the maximum of {MenuLimits.MaxMenusPerTenant} menus.");
        }

        if (categoryCount + sourceCategoryCount > MenuLimits.MaxCategoriesPerTenant)
        {
            problems.Add(TooManyMessage(sourceCategoryCount, categoryCount, MenuLimits.MaxCategoriesPerTenant, "category", "categories"));
        }

        if (itemCount + sourceItemCount > MenuLimits.MaxItemsPerTenant)
        {
            problems.Add(TooManyMessage(sourceItemCount, itemCount, MenuLimits.MaxItemsPerTenant, "item", "items"));
        }

        return problems;
    }

    /// <summary>The admin portal's message for a refused clone: "Couldn't clone "Lunch". You already have ...".</summary>
    public static string FailureMessage(string sourceName, IEnumerable<string> problems) =>
        $"Couldn't clone \"{sourceName}\". {string.Join(" ", problems)}";

    private static string TooManyMessage(int needed, int current, int max, string singular, string plural)
    {
        var room = Math.Max(0, max - current);
        var copied = $"This menu has {needed} {(needed == 1 ? singular : plural)}";
        return room == 0
            ? $"{copied}, but you already have the maximum of {max} {plural}."
            : $"{copied}, but you only have room for {room} more (the limit is {max} {plural} across all your menus).";
    }
}
