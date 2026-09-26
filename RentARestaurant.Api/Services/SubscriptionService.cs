using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RentARestaurant.Api.Contracts;
using RentARestaurant.Api.Data;
using RentARestaurant.Api.Infrastructure.Stripe;
using RentARestaurant.Api.Infrastructure.Tenancy;
using StripeApi = Stripe;

namespace RentARestaurant.Api.Services;

public sealed class SubscriptionService(
    AppDbContext dbContext,
    ITenantContext tenantContext,
    IOptions<StripeOptions> stripeOptions,
    ILogger<SubscriptionService> logger) : ISubscriptionService
{
    public async Task<AdminSubscriptionResponse> GetCurrentAsync(CancellationToken cancellationToken)
    {
        var tenant = await LoadTenantAsync(cancellationToken);

        return new AdminSubscriptionResponse(
            tenant.SubscriptionPlan,
            tenant.SubscriptionState,
            SubscriptionPlans.GetMonthlyPriceUsd(tenant.SubscriptionPlan),
            !string.IsNullOrWhiteSpace(tenant.StripeSubscriptionId));
    }

    public async Task<PreviewSubscriptionChangeResponse> PreviewChangeAsync(string targetPlan, CancellationToken cancellationToken)
    {
        var (tenant, normalizedTarget, changeType) = await ValidateChangeRequestAsync(targetPlan, cancellationToken);

        var subscriptionItemId = await GetSubscriptionItemIdAsync(tenant.StripeSubscriptionId!, cancellationToken);
        var targetPriceId = GetPriceId(normalizedTarget);
        var isUpgrade = changeType == "upgrade";

        var invoiceService = new StripeApi.InvoiceService();
        var upcoming = await invoiceService.CreatePreviewAsync(new StripeApi.InvoiceCreatePreviewOptions
        {
            Customer = tenant.StripeCustomerId,
            Subscription = tenant.StripeSubscriptionId,
            SubscriptionDetails = new StripeApi.InvoiceSubscriptionDetailsOptions
            {
                Items =
                [
                    new StripeApi.InvoiceSubscriptionDetailsItemOptions
                    {
                        Id = subscriptionItemId,
                        Price = targetPriceId,
                    }
                ],
                ProrationBehavior = isUpgrade ? "always_invoice" : "none",
            },
        }, cancellationToken: cancellationToken);

        var amountDueTodayUsd = isUpgrade ? upcoming.AmountDue / 100m : 0m;
        var effectiveDateUtc = isUpgrade ? DateTime.UtcNow : upcoming.PeriodEnd;

        return new PreviewSubscriptionChangeResponse(
            tenant.SubscriptionPlan,
            normalizedTarget,
            changeType,
            amountDueTodayUsd,
            SubscriptionPlans.GetMonthlyPriceUsd(normalizedTarget),
            effectiveDateUtc,
            upcoming.Currency ?? "usd");
    }

    public async Task<ChangeSubscriptionPlanResponse> ChangePlanAsync(string targetPlan, CancellationToken cancellationToken)
    {
        var (tenant, normalizedTarget, changeType) = await ValidateChangeRequestAsync(targetPlan, cancellationToken);

        var subscriptionItemId = await GetSubscriptionItemIdAsync(tenant.StripeSubscriptionId!, cancellationToken);
        var targetPriceId = GetPriceId(normalizedTarget);
        var isUpgrade = changeType == "upgrade";

        var subscriptionService = new StripeApi.SubscriptionService();
        var updateOptions = new StripeApi.SubscriptionUpdateOptions
        {
            Items =
            [
                new StripeApi.SubscriptionItemOptions
                {
                    Id = subscriptionItemId,
                    Price = targetPriceId,
                }
            ],
            // Upgrades charge the prorated difference immediately; declined payment leaves the Stripe subscription
            // on its original price (payment_behavior=error_if_incomplete), so we only touch the DB after success.
            ProrationBehavior = isUpgrade ? "always_invoice" : "none",
            PaymentBehavior = isUpgrade ? "error_if_incomplete" : null,
        };

        if (isUpgrade)
        {
            updateOptions.Expand = ["latest_invoice"];
        }

        decimal amountChargedUsd = 0m;

        try
        {
            var updated = await subscriptionService.UpdateAsync(tenant.StripeSubscriptionId, updateOptions, cancellationToken: cancellationToken);

            if (isUpgrade)
            {
                amountChargedUsd = (updated.LatestInvoice?.AmountPaid ?? 0) / 100m;
            }
        }
        catch (StripeApi.StripeException ex) when (isUpgrade)
        {
            logger.LogWarning(ex, "Stripe declined the upgrade charge for tenant {TenantId}", tenant.Id);
            throw new SubscriptionPaymentDeclinedException(
                "The upgrade payment was declined. Your subscription plan has not changed.");
        }

        tenant.SubscriptionPlan = normalizedTarget;
        await dbContext.SaveChangesAsync(cancellationToken);

        return new ChangeSubscriptionPlanResponse(tenant.SubscriptionPlan, tenant.SubscriptionState, amountChargedUsd);
    }

    private async Task<Domain.Entities.Tenant> LoadTenantAsync(CancellationToken cancellationToken)
    {
        var tenant = await dbContext.Tenants
            .SingleOrDefaultAsync(x => x.Id == tenantContext.TenantId!.Value, cancellationToken);

        if (tenant is null)
        {
            throw new InvalidOperationException("Tenant not found.");
        }

        return tenant;
    }

    private async Task<(Domain.Entities.Tenant Tenant, string NormalizedTarget, string ChangeType)> ValidateChangeRequestAsync(
        string targetPlan,
        CancellationToken cancellationToken)
    {
        var tenant = await LoadTenantAsync(cancellationToken);
        var normalizedTarget = targetPlan.Trim();

        if (normalizedTarget != SubscriptionPlans.SelfService && normalizedTarget != SubscriptionPlans.DoneForYou)
        {
            throw new ArgumentException("TargetPlan must be either 'Self-Service' or 'Done-For-You'.");
        }

        if (normalizedTarget == tenant.SubscriptionPlan)
        {
            throw new ArgumentException($"Tenant is already on the '{normalizedTarget}' plan.");
        }

        if (string.IsNullOrWhiteSpace(tenant.StripeCustomerId) || string.IsNullOrWhiteSpace(tenant.StripeSubscriptionId))
        {
            throw new SubscriptionNotLinkedException(
                "This tenant is not linked to a live Stripe subscription (it was likely provisioned outside real Stripe checkout).");
        }

        var changeType = normalizedTarget == SubscriptionPlans.DoneForYou ? "upgrade" : "downgrade";

        return (tenant, normalizedTarget, changeType);
    }

    private string GetPriceId(string plan) => plan == SubscriptionPlans.DoneForYou
        ? stripeOptions.Value.DoneForYouPriceId.Trim()
        : stripeOptions.Value.SelfServicePriceId.Trim();

    private async Task<string> GetSubscriptionItemIdAsync(string stripeSubscriptionId, CancellationToken cancellationToken)
    {
        var subscriptionService = new StripeApi.SubscriptionService();
        var subscription = await subscriptionService.GetAsync(stripeSubscriptionId, cancellationToken: cancellationToken);
        var item = subscription.Items.Data.FirstOrDefault();

        if (item is null)
        {
            throw new InvalidOperationException("Stripe subscription has no line items.");
        }

        return item.Id;
    }
}
