namespace RentARestaurant.Api.Domain.Entities;

public class SubscriptionEvent : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string? FromPlan { get; set; }
    public string? ToPlan { get; set; }
    public string? Status { get; set; }
    public string? StripeSubscriptionId { get; set; }
    public DateTime? EffectiveUtc { get; set; }
    public DateTime OccurredUtc { get; set; } = DateTime.UtcNow;

    public Tenant? Tenant { get; set; }
}

public static class SubscriptionEventTypes
{
    public const string Signup = "signup";
    public const string Upgrade = "upgrade";
    public const string DowngradeRequested = "downgrade_requested";
    public const string DowngradeCanceled = "downgrade_canceled";
    public const string DowngradeStarted = "downgrade_started";
    public const string CancelRequested = "cancel_requested";
    public const string CancelReverted = "cancel_reverted";
    public const string Canceled = "canceled";
}
