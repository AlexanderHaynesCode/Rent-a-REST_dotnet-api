using RentARestaurant.Api.Domain.Entities;

namespace RentARestaurant.Api.Services;

public interface ITenantSubscriptionService
{
    /// <summary>Creates or refreshes the tenant's subscription row after a successful Stripe checkout.</summary>
    Task RecordSignupAsync(Guid tenantId, string stripeSubscriptionId, CancellationToken cancellationToken);

    /// <summary>Refreshes an existing row from Stripe (customer.subscription.updated/deleted); no-op if unknown.</summary>
    Task SyncAsync(string stripeSubscriptionId, CancellationToken cancellationToken);

    /// <summary>Copies the current Stripe subscription state onto the row without saving.</summary>
    void ApplyStripeSubscription(TenantSubscription row, Stripe.Subscription subscription);
}
