using System.ComponentModel.DataAnnotations;

namespace RentARestaurant.Api.Contracts;

public sealed record AdminSubscriptionResponse(
    string SubscriptionPlan,
    string SubscriptionState,
    decimal MonthlyPriceUsd,
    bool IsLinkedToStripe,
    string? PendingPlan = null,
    DateTime? PendingPlanEffectiveUtc = null,
    string? BillingStatus = null);

public sealed record StripeRedirectRequest(
    [Required]
    [MaxLength(2048)]
    string ReturnUrl);

public sealed record StripeRedirectResponse(string Url);

public sealed record PreviewSubscriptionChangeResponse(
    string CurrentPlan,
    string TargetPlan,
    string ChangeType,
    decimal AmountDueTodayUsd,
    decimal NewRecurringAmountUsd,
    DateTime? EffectiveDateUtc,
    string Currency);

public sealed record ChangeSubscriptionPlanRequest(
    [Required]
    [RegularExpression("^(Self-Service|Done-For-You)$", ErrorMessage = "TargetPlan must be either 'Self-Service' or 'Done-For-You'.")]
    string TargetPlan);

public sealed record ChangeSubscriptionPlanResponse(
    string SubscriptionPlan,
    string SubscriptionState,
    decimal AmountChargedUsd,
    string? PendingPlan = null,
    DateTime? PendingPlanEffectiveUtc = null);
