namespace RentARestaurant.Api.Infrastructure.Stripe;

public class StripeOptions
{
    public const string SectionName = "Stripe";

    public string WebhookSigningSecret { get; set; } = string.Empty;
}
