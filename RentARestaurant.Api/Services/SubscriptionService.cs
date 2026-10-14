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
    IOptions<AdminOptions> adminOptions,
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
            row?.PendingPlanEffectiveUtc,
            row?.Status);
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

        if (changeType == Upgrade)
        {
            throw new ArgumentException("Upgrades must be paid through Stripe checkout.");
        }

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
            tenantSubscriptionService.AddEvent(
                tenant.Id, SubscriptionEventTypes.DowngradeCanceled, tenant.SubscriptionPlan, row.PendingPlan,
                row.Status, row.StripeSubscriptionId);
            row.PendingPlan = null;
            row.PendingPlanEffectiveUtc = null;
            row.UpdatedUtc = DateTime.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);

            return new ChangeSubscriptionPlanResponse(tenant.SubscriptionPlan, tenant.SubscriptionState, 0m);
        }

        await ScheduleDowngradeAsync(subscription, item, normalizedTarget, cancellationToken);

        row.PendingPlan = normalizedTarget;
        row.PendingPlanEffectiveUtc = item.CurrentPeriodEnd;
        row.UpdatedUtc = DateTime.UtcNow;
        tenantSubscriptionService.AddEvent(
            tenant.Id, SubscriptionEventTypes.DowngradeRequested, tenant.SubscriptionPlan, normalizedTarget,
            row.Status, row.StripeSubscriptionId, row.PendingPlanEffectiveUtc);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new ChangeSubscriptionPlanResponse(
            tenant.SubscriptionPlan, tenant.SubscriptionState, 0m, row.PendingPlan, row.PendingPlanEffectiveUtc);
    }

    public async Task<StripeRedirectResponse> CreateUpgradeCheckoutAsync(string returnUrl, CancellationToken cancellationToken)
    {
        var (tenant, row, targetPlan, changeType) = await ValidateChangeRequestAsync(SubscriptionPlans.DoneForYou, cancellationToken);
        if (changeType != Upgrade)
        {
            throw new ArgumentException("Only an upgrade to Done-For-You can be paid through Checkout.");
        }

        var preview = await PreviewChangeAsync(targetPlan, cancellationToken);
        var amountCents = (long)Math.Round(preview.AmountDueTodayUsd * 100m, MidpointRounding.AwayFromZero);
        if (amountCents <= 0)
        {
            throw new InvalidOperationException("Upgrade amount due is not positive; cannot create a payment session.");
        }

        var onboardingFeeIncluded = ShouldChargeOnboardingFee(targetPlan, row);
        var resolvedReturnUrl = ResolveReturnUrl(returnUrl);

        var session = await new StripeApi.Checkout.SessionService().CreateAsync(
            new StripeApi.Checkout.SessionCreateOptions
            {
                Mode = "payment",
                Customer = tenant.StripeCustomerId,
                ClientReferenceId = tenant.Id.ToString("D"),
                SuccessUrl = AppendQuery(resolvedReturnUrl, "checkout=success"),
                CancelUrl = AppendQuery(resolvedReturnUrl, "checkout=cancelled"),
                LineItems =
                [
                    new StripeApi.Checkout.SessionLineItemOptions
                    {
                        Quantity = 1,
                        PriceData = new StripeApi.Checkout.SessionLineItemPriceDataOptions
                        {
                            Currency = preview.Currency,
                            UnitAmount = amountCents,
                            ProductData = new StripeApi.Checkout.SessionLineItemPriceDataProductDataOptions
                            {
                                Name = onboardingFeeIncluded
                                    ? "Upgrade to Done-For-You (prorated plan change + onboarding fee)"
                                    : "Upgrade to Done-For-You (prorated plan change)",
                            },
                        },
                    }
                ],
                Metadata = new Dictionary<string, string>
                {
                    ["subscription_change"] = Upgrade,
                    ["tenant_id"] = tenant.Id.ToString("D"),
                    ["target_plan"] = targetPlan,
                    ["onboarding_fee_included"] = onboardingFeeIncluded ? "true" : "false",
                },
            },
            cancellationToken: cancellationToken);

        return new StripeRedirectResponse(session.Url);
    }

    public async Task<StripeRedirectResponse> CreateBillingPortalSessionAsync(string returnUrl, CancellationToken cancellationToken)
    {
        var tenant = await LoadTenantAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(tenant.StripeCustomerId))
        {
            throw new SubscriptionNotLinkedException("This tenant is not linked to a Stripe customer.");
        }

        var session = await new StripeApi.BillingPortal.SessionService().CreateAsync(
            new StripeApi.BillingPortal.SessionCreateOptions
            {
                Customer = tenant.StripeCustomerId,
                ReturnUrl = ResolveReturnUrl(returnUrl),
            },
            cancellationToken: cancellationToken);

        return new StripeRedirectResponse(session.Url);
    }

    public async Task CompleteUpgradeAsync(Guid tenantId, string targetPlan, bool onboardingFeeIncluded, CancellationToken cancellationToken)
    {
        if (targetPlan != SubscriptionPlans.DoneForYou)
        {
            throw new ArgumentException("Only upgrades to Done-For-You are supported.");
        }

        var tenant = await dbContext.Tenants.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Id == tenantId, cancellationToken)
            ?? throw new InvalidOperationException($"Tenant {tenantId} not found.");

        if (string.IsNullOrWhiteSpace(tenant.StripeSubscriptionId))
        {
            throw new SubscriptionNotLinkedException("Tenant has no Stripe subscription to upgrade.");
        }

        var subscriptionService = new StripeApi.SubscriptionService();
        var subscription = await subscriptionService.GetAsync(tenant.StripeSubscriptionId, cancellationToken: cancellationToken);
        var item = GetSubscriptionItem(subscription);
        var targetPriceId = GetPriceId(targetPlan);

        // Payment was already collected by Checkout, so the price switch must not generate another invoice.
        if (item.Price.Id != targetPriceId)
        {
            subscription = await subscriptionService.UpdateAsync(
                tenant.StripeSubscriptionId,
                new StripeApi.SubscriptionUpdateOptions
                {
                    Items = [new StripeApi.SubscriptionItemOptions { Id = item.Id, Price = targetPriceId }],
                    ProrationBehavior = "none",
                },
                cancellationToken: cancellationToken);
        }

        var row = await dbContext.TenantSubscriptions.IgnoreQueryFilters()
            .SingleOrDefaultAsync(x => x.TenantId == tenantId, cancellationToken);
        if (row is null)
        {
            row = new TenantSubscription { Id = Guid.NewGuid(), TenantId = tenantId, Plan = tenant.SubscriptionPlan };
            dbContext.TenantSubscriptions.Add(row);
        }

        tenantSubscriptionService.ApplyStripeSubscription(row, subscription);
        if (onboardingFeeIncluded && row.OnboardingFeePaidUtc is null)
        {
            row.OnboardingFeeCents = SubscriptionPlans.GetOnboardingFeeCents(targetPlan);
            row.OnboardingFeePaidUtc = DateTime.UtcNow;
        }

        // Skip on webhook retries where the tenant is already on the target plan.
        if (tenant.SubscriptionPlan != targetPlan)
        {
            tenantSubscriptionService.AddEvent(
                tenantId, SubscriptionEventTypes.Upgrade, tenant.SubscriptionPlan, targetPlan,
                row.Status, row.StripeSubscriptionId);
        }

        tenant.SubscriptionPlan = targetPlan;
        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Tenant {TenantId} upgraded to {Plan} after Checkout payment.", tenantId, targetPlan);
    }

    private string ResolveReturnUrl(string returnUrl)
    {
        if (!Uri.TryCreate(returnUrl?.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("ReturnUrl must be an absolute http(s) URL.");
        }

        var origin = uri.GetLeftPart(UriPartial.Authority);
        var allowed = adminOptions.Value.AllowedReturnOrigins
            .Any(x => string.Equals(x.Trim().TrimEnd('/'), origin, StringComparison.OrdinalIgnoreCase));
        if (!allowed)
        {
            throw new ArgumentException("ReturnUrl origin is not allowed.");
        }

        return uri.GetLeftPart(UriPartial.Path) + uri.Query;
    }

    private static string AppendQuery(string url, string query) =>
        url + (url.Contains('?') ? "&" : "?") + query;

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
