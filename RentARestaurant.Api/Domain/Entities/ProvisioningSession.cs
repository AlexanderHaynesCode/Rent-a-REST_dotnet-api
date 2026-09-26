namespace RentARestaurant.Api.Domain.Entities;

public class ProvisioningSession
{
    public Guid Id { get; set; }
    public string? CheckoutSessionId { get; set; }
    public string RestaurantName { get; set; } = string.Empty;
    public string OwnerEmail { get; set; } = string.Empty;
    public string OwnerExternalUserId { get; set; } = string.Empty;
    public string SubscriptionPlan { get; set; } = string.Empty;
    public string? RequestedSlug { get; set; }
    public string? CustomDomain { get; set; }
    public string Status { get; set; } = "pending_payment";
    public string? StripeCustomerId { get; set; }
    public string? StripeSubscriptionId { get; set; }
    public Guid? TenantId { get; set; }
    public string? TenantSlug { get; set; }
    public string? FailureReason { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
