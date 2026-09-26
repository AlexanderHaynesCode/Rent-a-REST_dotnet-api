namespace RentARestaurant.Api.Services;

/// <summary>Thrown when a subscription action requires a tenant that has completed real Stripe checkout.</summary>
public sealed class SubscriptionNotLinkedException(string message) : Exception(message);

/// <summary>Thrown when Stripe declines the immediate upgrade charge; the subscription price is left unchanged by Stripe.</summary>
public sealed class SubscriptionPaymentDeclinedException(string message) : Exception(message);
