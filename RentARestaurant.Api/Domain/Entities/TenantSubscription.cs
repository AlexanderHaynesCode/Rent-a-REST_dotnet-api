namespace RentARestaurant.Api.Domain.Entities;

public class TenantSubscription : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string StripeCustomerId { get; set; } = string.Empty;
    public string StripeSubscriptionId { get; set; } = string.Empty;
    public string? StripeSubscriptionItemId { get; set; }
    public string StripePriceId { get; set; } = string.Empty;
    public string Plan { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int MonthlyAmountCents { get; set; }
    public string Currency { get; set; } = "usd";
    public int OnboardingFeeCents { get; set; }
    public DateTime? OnboardingFeePaidUtc { get; set; }
    public DateTime? CurrentPeriodStartUtc { get; set; }
    public DateTime? CurrentPeriodEndUtc { get; set; }
    public bool CancelAtPeriodEnd { get; set; }
    public DateTime? CanceledUtc { get; set; }
    public string? LatestInvoiceId { get; set; }
    public DateTime? TrialEndUtc { get; set; }
    public string? PendingPlan { get; set; }
    public DateTime? PendingPlanEffectiveUtc { get; set; }
    public string? PreviousPlan { get; set; }
    public DateTime? PlanChangedUtc { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    public Tenant? Tenant { get; set; }
}
