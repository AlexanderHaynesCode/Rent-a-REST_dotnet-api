using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RentARestaurant.Api.Contracts;
using RentARestaurant.Api.Data;
using RentARestaurant.Api.Domain.Entities;
using RentARestaurant.Api.Infrastructure.Stripe;
using StripeApi = Stripe;

namespace RentARestaurant.Api.Services;

public sealed class TenantSubscriptionService(
    AppDbContext dbContext,
    IOptions<StripeOptions> stripeOptions,
    ILogger<TenantSubscriptionService> logger) : ITenantSubscriptionService
{
    public async Task RecordSignupAsync(Guid tenantId, string stripeSubscriptionId, CancellationToken cancellationToken)
    {
        var subscription = await new StripeApi.SubscriptionService()
            .GetAsync(stripeSubscriptionId, cancellationToken: cancellationToken);

        var row = await dbContext.TenantSubscriptions
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(x => x.TenantId == tenantId, cancellationToken);

        if (row is null)
        {
            row = new TenantSubscription { Id = Guid.NewGuid(), TenantId = tenantId };
            dbContext.TenantSubscriptions.Add(row);
        }

        ApplyStripeSubscription(row, subscription);

        if (row.OnboardingFeePaidUtc is null)
        {
            row.OnboardingFeeCents = SubscriptionPlans.GetOnboardingFeeCents(row.Plan);
            row.OnboardingFeePaidUtc = row.OnboardingFeeCents > 0 ? DateTime.UtcNow : null;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task SyncAsync(string stripeSubscriptionId, CancellationToken cancellationToken)
    {
        var row = await dbContext.TenantSubscriptions
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(x => x.StripeSubscriptionId == stripeSubscriptionId, cancellationToken);

        if (row is null)
        {
            logger.LogInformation("No tenant_subscriptions row for Stripe subscription {SubscriptionId}; skipping sync.", stripeSubscriptionId);
            return;
        }

        var subscription = await new StripeApi.SubscriptionService()
            .GetAsync(stripeSubscriptionId, cancellationToken: cancellationToken);

        var previousPlan = row.Plan;
        ApplyStripeSubscription(row, subscription);

        if (row.Plan != previousPlan)
        {
            var tenant = await dbContext.Tenants.SingleOrDefaultAsync(x => x.Id == row.TenantId, cancellationToken);
            if (tenant is not null)
            {
                tenant.SubscriptionPlan = row.Plan;
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public void ApplyStripeSubscription(TenantSubscription row, StripeApi.Subscription subscription)
    {
        var item = subscription.Items.Data.FirstOrDefault()
            ?? throw new InvalidOperationException("Stripe subscription has no line items.");

        var priceId = item.Price.Id;
        var plan = PlanForPriceId(priceId) ?? row.Plan;
        if (string.IsNullOrEmpty(plan))
        {
            throw new InvalidOperationException($"Stripe price '{priceId}' does not match a configured plan.");
        }

        if (!string.IsNullOrEmpty(row.Plan) && row.Plan != plan)
        {
            row.PreviousPlan = row.Plan;
            row.PlanChangedUtc = DateTime.UtcNow;
        }

        row.StripeCustomerId = subscription.CustomerId;
        row.StripeSubscriptionId = subscription.Id;
        row.StripeSubscriptionItemId = item.Id;
        row.StripePriceId = priceId;
        row.Plan = plan;
        row.Status = subscription.Status;
        row.MonthlyAmountCents = (int)(item.Price.UnitAmount ?? 0);
        row.Currency = subscription.Currency ?? "usd";
        row.CurrentPeriodStartUtc = item.CurrentPeriodStart;
        row.CurrentPeriodEndUtc = item.CurrentPeriodEnd;
        row.CancelAtPeriodEnd = subscription.CancelAtPeriodEnd;
        row.CanceledUtc = subscription.CanceledAt;
        row.LatestInvoiceId = subscription.LatestInvoiceId;
        row.TrialEndUtc = subscription.TrialEnd;
        row.UpdatedUtc = DateTime.UtcNow;

        // A scheduled change is finished once it applies, or if its Stripe schedule is gone.
        if (row.PendingPlan == plan || (row.PendingPlan is not null && subscription.ScheduleId is null))
        {
            row.PendingPlan = null;
            row.PendingPlanEffectiveUtc = null;
        }
    }

    private string? PlanForPriceId(string priceId)
    {
        if (priceId == stripeOptions.Value.DoneForYouPriceId.Trim())
        {
            return SubscriptionPlans.DoneForYou;
        }

        return priceId == stripeOptions.Value.SelfServicePriceId.Trim() ? SubscriptionPlans.SelfService : null;
    }
}
