using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RentARestaurant.Api.Contracts;
using RentARestaurant.Api.Data;
using RentARestaurant.Api.Domain.Entities;
using RentARestaurant.Api.Infrastructure.Stripe;
using RentARestaurant.Api.Infrastructure.Tenancy;
using StripeApi = Stripe;

namespace RentARestaurant.Api.Services;

public sealed class SubscriptionService(
    AppDbContext dbContext,
    ITenantContext tenantContext,
    ITenantSubscriptionService tenantSubscriptionService,
    IOptions<StripeOptions> stripeOptions,
    ILogger<SubscriptionService> logger) : ISubscriptionService
{
    private const string Upgrade = "upgrade";
    private const string Downgrade = "downgrade";
    private const string CancelDowngrade = "cancel_downgrade";

    public async Task<AdminSubscriptionResponse> GetCurrentAsync(CancellationToken cancellationToken)
    {
        var tenant = await LoadTenantAsync(cancellationToken);
        var row = await LoadSubscriptionRowAsync(tenant.Id, cancellationToken);

        return new AdminSubscriptionResponse(
            tenant.SubscriptionPlan,
            tenant.SubscriptionState,
            SubscriptionPlans.GetMonthlyPriceUsd(tenant.SubscriptionPlan),
            !string.IsNullOrWhiteSpace(tenant.StripeSubscriptionId),
            row?.PendingPlan,
            row?.PendingPlanEffectiveUtc);
    }

    public async Task<PreviewSubscriptionChangeResponse> PreviewChangeAsync(string targetPlan, CancellationToken cancellationToken)
    {
        var (tenant, row, normalizedTarget, changeType) = await ValidateChangeRequestAsync(targetPlan, cancellationToken);
        var newRecurringUsd = SubscriptionPlans.GetMonthlyPriceUsd(normalizedTarget);

        if (changeType == CancelDowngrade)
        {
            return new PreviewSubscriptionChangeResponse(
                tenant.SubscriptionPlan, normalizedTarget, changeType, 0m,
                newRecurringUsd, null, "usd");
        }

        var subscription = await new StripeApi.SubscriptionService()
            .GetAsync(tenant.StripeSubscriptionId, cancellationToken: cancellationToken);
        var item = GetSubscriptionItem(subscription);

        if (changeType == Downgrade)
        {
            // Downgrades are scheduled for the end of the paid period, so nothing is charged or prorated.
            return new PreviewSubscriptionChangeResponse(
                tenant.SubscriptionPlan, normalizedTarget, changeType, 0m,
                newRecurringUsd, item.CurrentPeriodEnd, subscription.Currency ?? "usd");
        }

        var upcoming = await new StripeApi.InvoiceService().CreatePreviewAsync(new StripeApi.InvoiceCreatePreviewOptions
        {
            Customer = tenant.StripeCustomerId,
            Subscription = tenant.StripeSubscriptionId,
            SubscriptionDetails = new StripeApi.InvoiceSubscriptionDetailsOptions
            {
                Items =
                [
                    new StripeApi.InvoiceSubscriptionDetailsItemOptions
                    {
                        Id = item.Id,
                        Price = GetPriceId(normalizedTarget),
                    }
                ],
                ProrationBehavior = "always_invoice",
            },
            InvoiceItems = ShouldChargeOnboardingFee(normalizedTarget, row)
                ? [new StripeApi.InvoiceUpcomingInvoiceItemOptions { Price = GetOnboardingFeePriceId() }]
                : null,
        }, cancellationToken: cancellationToken);

        return new PreviewSubscriptionChangeResponse(
            tenant.SubscriptionPlan,
            normalizedTarget,
            changeType,
            upcoming.AmountDue / 100m,
            newRecurringUsd,
            DateTime.UtcNow,
            upcoming.Currency ?? "usd");
    }

    public async Task<ChangeSubscriptionPlanResponse> ChangePlanAsync(string targetPlan, CancellationToken cancellationToken)
    {
        var (tenant, row, normalizedTarget, changeType) = await ValidateChangeRequestAsync(targetPlan, cancellationToken);

        var subscriptionService = new StripeApi.SubscriptionService();
        var subscription = await subscriptionService.GetAsync(tenant.StripeSubscriptionId, cancellationToken: cancellationToken);
        var item = GetSubscriptionItem(subscription);

        if (row is null)
        {
            // Tenant predates tenant_subscriptions; backfill from Stripe.
            row = new TenantSubscription { Id = Guid.NewGuid(), TenantId = tenant.Id, Plan = tenant.SubscriptionPlan };
            tenantSubscriptionService.ApplyStripeSubscription(row, subscription);
            dbContext.TenantSubscriptions.Add(row);
        }

        if (changeType == CancelDowngrade)
        {
            await ReleaseScheduleAsync(subscription, cancellationToken);
            row.PendingPlan = null;
            row.PendingPlanEffectiveUtc = null;
            row.UpdatedUtc = DateTime.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);

            return new ChangeSubscriptionPlanResponse(tenant.SubscriptionPlan, tenant.SubscriptionState, 0m);
        }

        if (changeType == Downgrade)
        {
            await ScheduleDowngradeAsync(subscription, item, normalizedTarget, cancellationToken);

            row.PendingPlan = normalizedTarget;
            row.PendingPlanEffectiveUtc = item.CurrentPeriodEnd;
            row.UpdatedUtc = DateTime.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);

            return new ChangeSubscriptionPlanResponse(
                tenant.SubscriptionPlan, tenant.SubscriptionState, 0m, row.PendingPlan, row.PendingPlanEffectiveUtc);
        }

        var updateOptions = new StripeApi.SubscriptionUpdateOptions
        {
            Items =
            [
                new StripeApi.SubscriptionItemOptions
                {
                    Id = item.Id,
                    Price = GetPriceId(normalizedTarget),
                }
            ],
            // A declined payment leaves the Stripe subscription on its original price (error_if_incomplete),
            // so the DB is only touched after success.
            ProrationBehavior = "always_invoice",
            PaymentBehavior = "error_if_incomplete",
            Expand = ["latest_invoice"],
        };

        var chargeOnboardingFee = ShouldChargeOnboardingFee(normalizedTarget, row);
        if (chargeOnboardingFee)
        {
            updateOptions.AddInvoiceItems =
                [new StripeApi.SubscriptionAddInvoiceItemOptions { Price = GetOnboardingFeePriceId() }];
        }

        StripeApi.Subscription updated;
        try
        {
            updated = await subscriptionService.UpdateAsync(tenant.StripeSubscriptionId, updateOptions, cancellationToken: cancellationToken);
        }
        catch (StripeApi.StripeException ex)
        {
            logger.LogWarning(ex, "Stripe declined the upgrade charge for tenant {TenantId}", tenant.Id);
            throw new SubscriptionPaymentDeclinedException(
                "The upgrade payment was declined. Your subscription plan has not changed.");
        }

        var amountChargedUsd = (updated.LatestInvoice?.AmountPaid ?? 0) / 100m;

        tenantSubscriptionService.ApplyStripeSubscription(row, updated);
        if (chargeOnboardingFee)
        {
            row.OnboardingFeeCents = SubscriptionPlans.GetOnboardingFeeCents(normalizedTarget);
            row.OnboardingFeePaidUtc = DateTime.UtcNow;
        }
        tenant.SubscriptionPlan = normalizedTarget;
        await dbContext.SaveChangesAsync(cancellationToken);

        return new ChangeSubscriptionPlanResponse(tenant.SubscriptionPlan, tenant.SubscriptionState, amountChargedUsd);
    }

    private async Task ScheduleDowngradeAsync(
        StripeApi.Subscription subscription,
        StripeApi.SubscriptionItem item,
        string targetPlan,
        CancellationToken cancellationToken)
    {
        var scheduleService = new StripeApi.SubscriptionScheduleService();
        var schedule = subscription.ScheduleId is null
            ? await scheduleService.CreateAsync(
                new StripeApi.SubscriptionScheduleCreateOptions { FromSubscription = subscription.Id },
                cancellationToken: cancellationToken)
            : await scheduleService.GetAsync(subscription.ScheduleId, cancellationToken: cancellationToken);

        var currentPhase = schedule.Phases.First();

        await scheduleService.UpdateAsync(schedule.Id, new StripeApi.SubscriptionScheduleUpdateOptions
        {
            EndBehavior = "release",
            ProrationBehavior = "none",
            Phases =
            [
                new StripeApi.SubscriptionSchedulePhaseOptions
                {
                    Items = [new StripeApi.SubscriptionSchedulePhaseItemOptions { Price = item.Price.Id, Quantity = 1 }],
                    StartDate = currentPhase.StartDate,
                    EndDate = currentPhase.EndDate,
                },
                new StripeApi.SubscriptionSchedulePhaseOptions
                {
                    Items = [new StripeApi.SubscriptionSchedulePhaseItemOptions { Price = GetPriceId(targetPlan), Quantity = 1 }],
                    Duration = new StripeApi.SubscriptionSchedulePhaseDurationOptions { Interval = "month", IntervalCount = 1 },
                },
            ],
        }, cancellationToken: cancellationToken);
    }

    private static async Task ReleaseScheduleAsync(StripeApi.Subscription subscription, CancellationToken cancellationToken)
    {
        if (subscription.ScheduleId is not null)
        {
            await new StripeApi.SubscriptionScheduleService()
                .ReleaseAsync(subscription.ScheduleId, cancellationToken: cancellationToken);
        }
    }

    private async Task<Tenant> LoadTenantAsync(CancellationToken cancellationToken)
    {
        var tenant = await dbContext.Tenants
            .SingleOrDefaultAsync(x => x.Id == tenantContext.TenantId!.Value, cancellationToken);

        if (tenant is null)
        {
            throw new InvalidOperationException("Tenant not found.");
        }

        return tenant;
    }

    private Task<TenantSubscription?> LoadSubscriptionRowAsync(Guid tenantId, CancellationToken cancellationToken) =>
        dbContext.TenantSubscriptions.SingleOrDefaultAsync(x => x.TenantId == tenantId, cancellationToken);

    private async Task<(Tenant Tenant, TenantSubscription? Row, string NormalizedTarget, string ChangeType)> ValidateChangeRequestAsync(
        string targetPlan,
        CancellationToken cancellationToken)
    {
        var tenant = await LoadTenantAsync(cancellationToken);
        var row = await LoadSubscriptionRowAsync(tenant.Id, cancellationToken);
        var normalizedTarget = targetPlan.Trim();

        if (normalizedTarget != SubscriptionPlans.SelfService && normalizedTarget != SubscriptionPlans.DoneForYou)
        {
            throw new ArgumentException("TargetPlan must be either 'Self-Service' or 'Done-For-You'.");
        }

        if (string.IsNullOrWhiteSpace(tenant.StripeCustomerId) || string.IsNullOrWhiteSpace(tenant.StripeSubscriptionId))
        {
            throw new SubscriptionNotLinkedException(
                "This tenant is not linked to a live Stripe subscription (it was likely provisioned outside real Stripe checkout).");
        }

        if (normalizedTarget == tenant.SubscriptionPlan)
        {
            // Re-selecting the current plan while a downgrade is scheduled cancels that downgrade.
            if (row?.PendingPlan is not null)
            {
                return (tenant, row, normalizedTarget, CancelDowngrade);
            }

            throw new ArgumentException($"Tenant is already on the '{normalizedTarget}' plan.");
        }

        if (row?.PendingPlan == normalizedTarget)
        {
            throw new ArgumentException($"A downgrade to '{normalizedTarget}' is already scheduled.");
        }

        var changeType = normalizedTarget == SubscriptionPlans.DoneForYou ? Upgrade : Downgrade;

        return (tenant, row, normalizedTarget, changeType);
    }

    private static bool ShouldChargeOnboardingFee(string targetPlan, TenantSubscription? row) =>
        targetPlan == SubscriptionPlans.DoneForYou
        && SubscriptionPlans.GetOnboardingFeeCents(targetPlan) > 0
        && row?.OnboardingFeePaidUtc is null;

    private string GetOnboardingFeePriceId()
    {
        var priceId = stripeOptions.Value.DoneForYouOnboardingFeePriceId.Trim();
        return string.IsNullOrWhiteSpace(priceId)
            ? throw new InvalidOperationException("Stripe:DoneForYouOnboardingFeePriceId is not configured.")
            : priceId;
    }

    private string GetPriceId(string plan) => plan == SubscriptionPlans.DoneForYou
        ? stripeOptions.Value.DoneForYouPriceId.Trim()
        : stripeOptions.Value.SelfServicePriceId.Trim();

    private static StripeApi.SubscriptionItem GetSubscriptionItem(StripeApi.Subscription subscription) =>
        subscription.Items.Data.FirstOrDefault()
            ?? throw new InvalidOperationException("Stripe subscription has no line items.");
}
