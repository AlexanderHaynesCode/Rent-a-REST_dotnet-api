using System.ComponentModel.DataAnnotations;

namespace RentARestaurant.Api.Contracts;

public sealed record AdminTenantSummaryResponse(
    Guid TenantId,
    string Slug,
    string Name,
    string? CustomDomain,
    bool IsActive,
    string SubscriptionPlan,
    string SubscriptionState);

public sealed record UpdateSubscriptionRequest(
    [RegularExpression("^(Self-Service|Done-For-You)$", ErrorMessage = "SubscriptionPlan must be either 'Self-Service' or 'Done-For-You'.")]
    string SubscriptionPlan,
    [RegularExpression("^(active|trialing)$", ErrorMessage = "SubscriptionState must be either 'active' or 'trialing'.")]
    string? SubscriptionState);

public sealed record AdminBootstrapResponse(
    AdminTenantSummaryResponse Tenant,
    PublicRestaurantResponse Restaurant);

/// <summary>The menu created by a clone, with its copied categories and items (all with new ids).</summary>
public sealed record ClonedMenuResponse(
    PublicMenuResponse Menu,
    IReadOnlyList<PublicMenuCategoryResponse> Categories);

public sealed record AdminDateSpecificHourResponse(
    DateOnly Date,
    DayOfWeek DayOfWeek,
    string OpenTime,
    string CloseTime,
    bool IsClosed);