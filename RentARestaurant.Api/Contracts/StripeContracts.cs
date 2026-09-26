using System.ComponentModel.DataAnnotations;

namespace RentARestaurant.Api.Contracts;

public sealed record CreateCheckoutSessionRequest(
    string RestaurantName,
    string OwnerEmail,
    string OwnerExternalUserId,
    [RegularExpression("^(Self-Service|Done-For-You)$", ErrorMessage = "SubscriptionPlan must be either 'Self-Service' or 'Done-For-You'.")]
    string SubscriptionPlan,
    string? RequestedSlug,
    string? CustomDomain);

public sealed record CreateCheckoutSessionResponse(
    string CheckoutSessionId,
    string CheckoutUrl);

public sealed record ProvisioningStatusResponse(
    string CheckoutSessionId,
    string Status,
    Guid? TenantId,
    string? Slug,
    string? Message);
