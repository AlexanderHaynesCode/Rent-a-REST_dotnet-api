namespace RentARestaurant.Api.Infrastructure.Stripe;

public class StripeOptions
{
    public const string SectionName = "Stripe";

    public string SecretKey { get; set; } = string.Empty;
    public string WebhookSigningSecret { get; set; } = string.Empty;
    public string SelfServicePriceId { get; set; } = string.Empty;
    public string DoneForYouPriceId { get; set; } = string.Empty;
    public string DoneForYouOnboardingFeePriceId { get; set; } = string.Empty;
}
