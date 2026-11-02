using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RentARestaurant.Api.Contracts;
using RentARestaurant.Api.Data;
using RentARestaurant.Api.Domain.Entities;
using RentARestaurant.Api.Infrastructure.Email;
using RentARestaurant.Api.Infrastructure.Stripe;
using StripeApi = Stripe;

namespace RentARestaurant.Api.Services;

public sealed class TenantSubscriptionService(
    AppDbContext dbContext,
    IOptions<StripeOptions> stripeOptions,
    IEmailService emailService,
    ILogger<TenantSubscriptionService> logger) : ITenantSubscriptionService
{
    private const string Canceled = "canceled";
    private const string Suspended = "suspended";
    private const string PastDue = "past_due";
    private const string Unpaid = "unpaid";
    private const int GraceDays = 7;

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
        var previousPendingPlan = row.PendingPlan;
        var previousCancelAtPeriodEnd = row.CancelAtPeriodEnd;
        var previousStatus = row.Status;
        ApplyStripeSubscription(row, subscription);

        // Upgrades are logged by CompleteUpgradeAsync, not here.
        if (row.Plan != previousPlan && previousPendingPlan == row.Plan)
        {
            AddSubscriptionEvent(row.TenantId, SubscriptionEventTypes.DowngradeStarted, previousPlan, row.Plan, row.Status, row.StripeSubscriptionId);
        }

        if (row.CancelAtPeriodEnd != previousCancelAtPeriodEnd)
        {
            AddSubscriptionEvent(
                row.TenantId,
                row.CancelAtPeriodEnd ? SubscriptionEventTypes.CancelRequested : SubscriptionEventTypes.CancelReverted,
                null, null, row.Status, row.StripeSubscriptionId,
                row.CancelAtPeriodEnd ? row.CurrentPeriodEndUtc : null);
        }

        var isCanceled = row.Status == Canceled;
        var isNewlyCanceled = isCanceled && previousStatus != Canceled;

        if (isNewlyCanceled)
        {
            AddSubscriptionEvent(row.TenantId, SubscriptionEventTypes.Canceled, null, null, row.Status, row.StripeSubscriptionId);
        }

        // The grace clock starts once per delinquency episode and is not reset by further failed retries.
        var now = DateTime.UtcNow;
        var startedGrace = false;
        if ((row.Status == PastDue || row.Status == Unpaid) && row.PaymentFailedUtc is null)
        {
            row.PaymentFailedUtc = now;
            row.GraceEndsUtc = now.AddDays(GraceDays);
            row.GraceReminderSentUtc = null;
            startedGrace = true;
            AddSubscriptionEvent(row.TenantId, SubscriptionEventTypes.PaymentFailed, null, null, row.Status, row.StripeSubscriptionId, row.GraceEndsUtc);
        }

        var recovered = !isCanceled && row.Status is "active" or "trialing" && row.PaymentFailedUtc is not null;
        if (recovered)
        {
            row.PaymentFailedUtc = null;
            row.GraceEndsUtc = null;
            row.GraceReminderSentUtc = null;
        }

        var suspendedNow = false;
        var restoredNow = false;
        var needsTenant = row.Plan != previousPlan || isNewlyCanceled || row.Status == Unpaid || recovered;
        Tenant? tenant = null;
        if (needsTenant)
        {
            tenant = await dbContext.Tenants.SingleOrDefaultAsync(x => x.Id == row.TenantId, cancellationToken);
            if (tenant is not null)
            {
                if (row.Plan != previousPlan || isNewlyCanceled)
                {
                    tenant.SubscriptionPlan = row.Plan;
                }

                if (isCanceled)
                {
                    // Data is retained; only access is turned off.
                    if (isNewlyCanceled)
                    {
                        tenant.SubscriptionState = Canceled;
                        tenant.IsActive = false;
                    }
                }
                else if (row.Status == Unpaid && tenant.SubscriptionState != Suspended)
                {
                    tenant.SubscriptionState = Suspended;
                    tenant.IsActive = false;
                    suspendedNow = true;
                    AddSubscriptionEvent(row.TenantId, SubscriptionEventTypes.Suspended, null, null, row.Status, row.StripeSubscriptionId);
                }
                else if (recovered)
                {
                    AddSubscriptionEvent(row.TenantId, SubscriptionEventTypes.PaymentRecovered, null, null, row.Status, row.StripeSubscriptionId);
                    if (tenant.SubscriptionState == Suspended)
                    {
                        tenant.SubscriptionState = "active";
                        tenant.IsActive = true;
                        restoredNow = true;
                    }
                }
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        if (tenant is null && startedGrace)
        {
            tenant = await dbContext.Tenants.SingleOrDefaultAsync(x => x.Id == row.TenantId, cancellationToken);
        }

        if (tenant is not null && (startedGrace || suspendedNow || restoredNow))
        {
            await SendPaymentEmailsAsync(tenant, row, startedGrace, suspendedNow, restoredNow, cancellationToken);
        }
    }

    private async Task SendPaymentEmailsAsync(
        Tenant tenant, TenantSubscription row, bool startedGrace, bool suspendedNow, bool restoredNow, CancellationToken cancellationToken)
    {
        try
        {
            var ownerEmail = await dbContext.TenantUsers.IgnoreQueryFilters()
                .Where(x => x.TenantId == tenant.Id)
                .OrderBy(x => x.Role == "Owner" ? 0 : 1)
                .Select(x => x.Email)
                .FirstOrDefaultAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(ownerEmail))
            {
                return;
            }

            if (startedGrace && !suspendedNow && row.GraceEndsUtc is { } graceEnds)
            {
                await emailService.SendPaymentFailedEmailAsync(ownerEmail, tenant.Name, graceEnds, cancellationToken);
            }

            if (suspendedNow)
            {
                await emailService.SendAccountSuspendedEmailAsync(ownerEmail, tenant.Name, cancellationToken);
            }

            if (restoredNow)
            {
                await emailService.SendPaymentRecoveredEmailAsync(ownerEmail, tenant.Name, tenant.Slug, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            // State is already committed; a failed email must not make Stripe retry the webhook.
            logger.LogWarning(ex, "Failed to send payment email for tenant {TenantId}", tenant.Id);
        }
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

    public void AddSubscriptionEvent(
        Guid tenantId,
        string eventType,
        string? fromPlan,
        string? toPlan,
        string? status,
        string? stripeSubscriptionId,
        DateTime? effectiveUtc = null)
    {
        dbContext.SubscriptionEvents.Add(new SubscriptionEvent
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            EventType = eventType,
            FromPlan = fromPlan,
            ToPlan = toPlan,
            Status = status,
            StripeSubscriptionId = stripeSubscriptionId,
            EffectiveUtc = effectiveUtc,
            OccurredUtc = DateTime.UtcNow,
        });
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
