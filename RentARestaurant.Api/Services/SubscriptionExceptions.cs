namespace RentARestaurant.Api.Services;

/// <summary>Thrown when a subscription action requires a tenant that has completed real Stripe checkout.</summary>
public sealed class SubscriptionNotLinkedException(string message) : Exception(message);
