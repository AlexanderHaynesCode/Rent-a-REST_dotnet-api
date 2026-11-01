using RentARestaurant.Api.Contracts;

namespace RentARestaurant.Api.Services;

public interface ISubscriptionService
{
    Task<AdminSubscriptionResponse> GetCurrentAsync(CancellationToken cancellationToken);

    Task<PreviewSubscriptionChangeResponse> PreviewChangeAsync(string targetPlan, CancellationToken cancellationToken);

    /// <summary>Schedules a downgrade or cancels a pending one. Upgrades go through <see cref="CreateUpgradeCheckoutAsync"/>.</summary>
    Task<ChangeSubscriptionPlanResponse> ChangePlanAsync(string targetPlan, CancellationToken cancellationToken);

    /// <summary>Schedules or reverts cancellation at the end of the paid period. Idempotent.</summary>
    Task<SetSubscriptionCancellationResponse> SetCancellationAsync(bool cancelAtPeriodEnd, CancellationToken cancellationToken);

    Task<StripeRedirectResponse> CreateUpgradeCheckoutAsync(string returnUrl, CancellationToken cancellationToken);

    Task<StripeRedirectResponse> CreateBillingPortalSessionAsync(string returnUrl, CancellationToken cancellationToken);

    /// <summary>Returns null when the user has no tenant membership.</summary>
    Task<ReactivationInfoResponse?> GetReactivationInfoAsync(string externalUserId, CancellationToken cancellationToken);

    /// <summary>Returns null when the user has no tenant membership; throws InvalidOperationException if the tenant is not canceled.</summary>
    Task<StripeRedirectResponse?> CreateReactivationCheckoutAsync(string externalUserId, string plan, string returnUrl, CancellationToken cancellationToken);

    /// <summary>Attaches the new Stripe subscription to a canceled tenant and reactivates it. Safe to call repeatedly.</summary>
    Task CompleteReactivationAsync(Guid tenantId, string targetPlan, bool onboardingFeeIncluded, string stripeSubscriptionId, CancellationToken cancellationToken);

    /// <summary>Applies a paid upgrade to the Stripe subscription and the tenant. Safe to call repeatedly.</summary>
    Task CompleteUpgradeAsync(Guid tenantId, string targetPlan, bool onboardingFeeIncluded, CancellationToken cancellationToken);
}
