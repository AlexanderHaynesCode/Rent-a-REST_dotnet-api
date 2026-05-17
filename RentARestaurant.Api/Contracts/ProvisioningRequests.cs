using System.ComponentModel.DataAnnotations;

namespace RentARestaurant.Api.Contracts;

public static class SubscriptionPlans
{
    public const string SelfService = "Self-Service";
    public const string DoneForYou = "Done-For-You";
}

public sealed record ProvisionTenantRequest(
    string RestaurantName,
    string OwnerEmail,
    string OwnerExternalUserId,
    [RegularExpression("^(Self-Service|Done-For-You)$", ErrorMessage = "SubscriptionPlan must be either 'Self-Service' or 'Done-For-You'.")]
    string SubscriptionPlan,
    string? RequestedSlug,
    string? StripeCustomerId,
    string? StripeSubscriptionId,
    string? CustomDomain);

public sealed record StripeWebhookRequest(
    string EventType,
    string? EventId,
    ProvisionTenantRequest? CheckoutCompleted);

public sealed record ProvisionTenantResponse(
    Guid TenantId,
    string Slug,
    bool IsActive,
    string MediaNamespace);
