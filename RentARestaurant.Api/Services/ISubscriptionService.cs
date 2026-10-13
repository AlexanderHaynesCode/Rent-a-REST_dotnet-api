using RentARestaurant.Api.Contracts;

namespace RentARestaurant.Api.Services;

public interface ISubscriptionService
{
    Task<AdminSubscriptionResponse> GetCurrentAsync(CancellationToken cancellationToken);

    Task<PreviewSubscriptionChangeResponse> PreviewChangeAsync(string targetPlan, CancellationToken cancellationToken);

    /// <summary>Schedules a downgrade or cancels a pending one. Upgrades go through <see cref="CreateUpgradeCheckoutAsync"/>.</summary>
    Task<ChangeSubscriptionPlanResponse> ChangePlanAsync(string targetPlan, CancellationToken cancellationToken);

    Task<StripeRedirectResponse> CreateUpgradeCheckoutAsync(string returnUrl, CancellationToken cancellationToken);

    Task<StripeRedirectResponse> CreateBillingPortalSessionAsync(string returnUrl, CancellationToken cancellationToken);

    /// <summary>Applies a paid upgrade to the Stripe subscription and the tenant. Safe to call repeatedly.</summary>
    Task CompleteUpgradeAsync(Guid tenantId, string targetPlan, bool onboardingFeeIncluded, CancellationToken cancellationToken);
}
