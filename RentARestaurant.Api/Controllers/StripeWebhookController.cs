using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using RentARestaurant.Api.Contracts;
using RentARestaurant.Api.Infrastructure.Stripe;
using RentARestaurant.Api.Services;

namespace RentARestaurant.Api.Controllers;

[ApiController]
[Route("api/stripe/webhooks")]
public class StripeWebhookController(
    ITenantProvisioningService tenantProvisioningService,
    IOptions<StripeOptions> stripeOptions,
    ILogger<StripeWebhookController> logger) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Handle([FromBody] StripeWebhookRequest payload, CancellationToken cancellationToken)
    {
        if (!Request.Headers.TryGetValue("Stripe-Signature", out var signature)
            || string.IsNullOrWhiteSpace(signature))
        {
            return Unauthorized(new { Error = "Missing Stripe-Signature header." });
        }

        if (!string.IsNullOrWhiteSpace(stripeOptions.Value.WebhookSigningSecret)
            && !string.Equals(signature.ToString().Trim(), stripeOptions.Value.WebhookSigningSecret, StringComparison.Ordinal))
        {
            return Unauthorized(new { Error = "Invalid webhook signature." });
        }

        if (payload.EventType != "checkout.session.completed")
        {
            logger.LogInformation("Ignoring Stripe event type {EventType}", payload.EventType);
            return Ok(new { Processed = false });
        }

        if (payload.CheckoutCompleted is null)
        {
            return BadRequest(new { Error = "Checkout payload is required for checkout.session.completed." });
        }

        var result = await tenantProvisioningService.ProvisionTenantAsync(payload.CheckoutCompleted, cancellationToken);

        return Ok(new
        {
            Processed = true,
            payload.EventId,
            result.TenantId,
            result.Slug,
            result.MediaNamespace
        });
    }
}
