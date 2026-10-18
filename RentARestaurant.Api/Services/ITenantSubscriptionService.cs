using RentARestaurant.Api.Domain.Entities;

namespace RentARestaurant.Api.Services;

public interface ITenantSubscriptionService
{
    /// <summary>Refreshes an existing row from Stripe (customer.subscription.updated/deleted); no-op if unknown.</summary>
    Task SyncAsync(string stripeSubscriptionId, CancellationToken cancellationToken);

    /// <summary>Copies the current Stripe subscription state onto the row without saving.</summary>
    void ApplyStripeSubscription(TenantSubscription row, Stripe.Subscription subscription);

    /// <summary>Stages a subscription_events row; the caller's SaveChangesAsync persists it.</summary>
    void AddEvent(
        Guid tenantId,
        string eventType,
        string? fromPlan,
        string? toPlan,
        string? status,
        string? stripeSubscriptionId,
        DateTime? effectiveUtc = null);
}
