using System.Text.Json;

namespace RentARestaurant.Api.Domain;

/// <summary>One size of a menu item, e.g. "Small" at $9.99 or "12 pc" at $9.49.</summary>
public sealed record MenuItemPriceOption(string Label, decimal Price);

/// <summary>
/// A menu item has either one price (no sizes, shown as is) or <see cref="MinOptions"/> to <see cref="MaxOptions"/>
/// sizes, each with a label and a price, shown in the stored order. While an item has sizes its <c>price</c> column
/// holds the first size's price. Stored in <c>menu_items.price_options</c> (JSONB, NULL = one price), whose CHECK
/// constraint mirrors the size count. The rules are mirrored by admin-client-app (<c>domain/menuItemPrices.ts</c>)
/// and agent-validator-service.
/// </summary>
public static class MenuItemPriceRules
{
    public const int MinOptions = 2;
    public const int MaxOptions = 4;
    public const int MaxLabelLength = 24;

    /// <summary>The largest value the price column's NUMERIC(10, 2) holds.</summary>
    public const decimal MaxPrice = 99_999_999.99m;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Returns what is wrong with an item's sizes, or null when they are valid. Null or empty means one price.</summary>
    public static string? Validate(IReadOnlyList<MenuItemPriceOption>? options)
    {
        if (options is null || options.Count == 0)
        {
            return null;
        }

        if (options.Count < MinOptions)
        {
            return $"An item with sizes needs at least {MinOptions} of them. Remove the sizes to use a single price.";
        }

        if (options.Count > MaxOptions)
        {
            return $"An item can have up to {MaxOptions} sizes.";
        }

        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var option in options)
        {
            var label = option?.Label?.Trim();
            if (string.IsNullOrEmpty(label))
            {
                return "Each size needs a name, like Small or 12 pc.";
            }

            if (label.Length > MaxLabelLength)
            {
                return $"Size names must be {MaxLabelLength} characters or fewer.";
            }

            if (!labels.Add(label))
            {
                return "Each size of an item needs a different name.";
            }

            if (option!.Price is < 0 or > MaxPrice)
            {
                return "Each size needs a price of $0.00 or more.";
            }
        }

        return null;
    }

    /// <summary>Trims the labels and rounds the prices to cents. Null or empty becomes null (one price).</summary>
    public static IReadOnlyList<MenuItemPriceOption>? Normalize(IReadOnlyList<MenuItemPriceOption>? options) =>
        options is null || options.Count == 0
            ? null
            : options
                .Select(option => new MenuItemPriceOption(
                    (option.Label ?? string.Empty).Trim(),
                    Math.Round(option.Price, 2, MidpointRounding.AwayFromZero)))
                .ToList();

    /// <summary>"$9.99", or "Small $9.99, Large $13.99" for an item with sizes (used in the agent's result email).</summary>
    public static string Describe(decimal price, IReadOnlyList<MenuItemPriceOption>? options) =>
        options is { Count: > 0 }
            ? string.Join(", ", options.Select(option => $"{option.Label} ${option.Price:0.00}"))
            : $"${price:0.00}";

    /// <summary>The JSONB text stored in <c>menu_items.price_options</c>: [{"label":"Small","price":9.99}, ...].</summary>
    public static string ToJson(IReadOnlyList<MenuItemPriceOption> options) => JsonSerializer.Serialize(options, JsonOptions);

    public static IReadOnlyList<MenuItemPriceOption>? FromJson(string json) =>
        JsonSerializer.Deserialize<List<MenuItemPriceOption>>(json, JsonOptions);
}
