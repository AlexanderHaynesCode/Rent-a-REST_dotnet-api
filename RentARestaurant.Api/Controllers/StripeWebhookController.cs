using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    IStripeCheckoutService stripeCheckoutService,
    IOptions<StripeOptions> stripeOptions,
    IWebHostEnvironment environment,
    ILogger<StripeWebhookController> logger) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Handle(CancellationToken cancellationToken)
    {
        logger.LogInformation("Alexxx Handling Stripe webhook request.");
        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var rawBody = await reader.ReadToEndAsync(cancellationToken);

        var browserTestTrigger = string.Equals(
            Request.Headers["X-Stripe-Test-Trigger"].ToString().Trim(),
            "browser",
            StringComparison.OrdinalIgnoreCase);
        logger.LogInformation("environment.IsDevelopment(): {IsDevelopment}", environment.IsDevelopment());
        if (environment.IsDevelopment() && browserTestTrigger)
        {
            logger.LogInformation("Accepting development browser trigger for Stripe webhook test.");

            var payload = JsonSerializer.Deserialize<StripeWebhookRequest>(rawBody);
            if (payload is null)
            {
                return BadRequest(new { Error = "Invalid webhook payload." });
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

        if (!Request.Headers.TryGetValue("Stripe-Signature", out var signatureHeader) || string.IsNullOrWhiteSpace(signatureHeader.ToString()))
        {
            return Unauthorized(new { Error = "Missing Stripe-Signature header." });
        }

        var webhookSecret = stripeOptions.Value.WebhookSigningSecret.Trim();
        if (string.IsNullOrWhiteSpace(webhookSecret))
        {
            return Unauthorized(new { Error = "Webhook signing secret is not configured." });
        }

        if (!VerifyStripeSignature(rawBody, signatureHeader.ToString(), webhookSecret))
        {
            return Unauthorized(new { Error = "Invalid webhook signature." });
        }

        using var document = JsonDocument.Parse(rawBody);
        var root = document.RootElement;
        var eventType = ReadString(root, "type") ?? ReadString(root, "eventType");
        logger.LogInformation("Handling Stripe event of type {EventType}", eventType);
        if (!string.Equals(eventType, "checkout.session.completed", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("Ignoring Stripe event type {EventType}", eventType);
            return Ok(new { Processed = false });
        }

        if (!TryReadCheckoutSessionObject(root, out var checkoutSessionElement))
        {
            return BadRequest(new { Error = "checkout.session.completed payload is missing the session object." });
        }

        var provisioningSessionIdValue = ReadMetadata(checkoutSessionElement, "provisioning_session_id");
        if (!Guid.TryParse(provisioningSessionIdValue, out var provisioningSessionId))
        {
            return BadRequest(new { Error = "checkout.session.completed payload is missing provisioning_session_id metadata." });
        }

        var provisioningSession = await stripeCheckoutService.GetProvisioningSessionByIdAsync(provisioningSessionId, cancellationToken);
        if (provisioningSession is null)
        {
            return NotFound(new { Error = "Provisioning session not found." });
        }

        if (string.Equals(provisioningSession.Status, "ready", StringComparison.OrdinalIgnoreCase) && provisioningSession.TenantId.HasValue)
        {
            return Ok(new
            {
                Processed = true,
                TenantId = provisioningSession.TenantId,
                Slug = provisioningSession.TenantSlug,
                MediaNamespace = $"tenants/{provisioningSession.TenantId:D}"
            });
        }

        await stripeCheckoutService.MarkProvisioningProcessingAsync(provisioningSession.Id, cancellationToken);

        var stripeCustomerId = ReadString(checkoutSessionElement, "customer");
        var stripeSubscriptionId = ReadString(checkoutSessionElement, "subscription");

        var checkoutCompleted = new ProvisionTenantRequest(
            provisioningSession.RestaurantName,
            provisioningSession.OwnerEmail,
            provisioningSession.OwnerExternalUserId,
            provisioningSession.SubscriptionPlan,
            provisioningSession.RequestedSlug,
            stripeCustomerId,
            stripeSubscriptionId,
            provisioningSession.CustomDomain);

        try
        {
            var result = await tenantProvisioningService.ProvisionTenantAsync(checkoutCompleted, cancellationToken);

            await stripeCheckoutService.MarkProvisioningCompletedAsync(
                provisioningSession.Id,
                result.TenantId,
                result.Slug,
                stripeCustomerId,
                stripeSubscriptionId,
                cancellationToken);

            return Ok(new
            {
                Processed = true,
                TenantId = result.TenantId,
                Slug = result.Slug,
                MediaNamespace = result.MediaNamespace
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to provision tenant from Stripe checkout session {ProvisioningSessionId}", provisioningSession.Id);
            await stripeCheckoutService.MarkProvisioningFailedAsync(provisioningSession.Id, ex.Message, cancellationToken);
            return StatusCode(StatusCodes.Status500InternalServerError, new { Error = "Failed to provision tenant." });
        }

        static bool VerifyStripeSignature(string rawBody, string signatureHeader, string webhookSecret)
        {
            var parts = signatureHeader
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => part.Split('=', 2))
                .Where(part => part.Length == 2)
                .ToDictionary(part => part[0], part => part[1], StringComparer.OrdinalIgnoreCase);

            if (!parts.TryGetValue("t", out var timestamp) || !parts.TryGetValue("v1", out var signature))
            {
                return false;
            }

            if (!long.TryParse(timestamp, out var unixTimestamp))
            {
                return false;
            }

            var timestampAge = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - unixTimestamp;
            if (Math.Abs(timestampAge) > 300)
            {
                return false;
            }

            var signedPayload = $"{timestamp}.{rawBody}";
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(webhookSecret));
            var computed = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(signedPayload))).ToLowerInvariant();

            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(computed),
                Encoding.UTF8.GetBytes(signature));
        }

        static bool TryReadCheckoutSessionObject(JsonElement rootElement, out JsonElement checkoutSessionElement)
        {
            checkoutSessionElement = default;

            if (rootElement.TryGetProperty("data", out var dataElement)
                && dataElement.ValueKind == JsonValueKind.Object
                && dataElement.TryGetProperty("object", out var objectElement)
                && objectElement.ValueKind == JsonValueKind.Object)
            {
                checkoutSessionElement = objectElement;
                return true;
            }

            return false;
        }

        static string? ReadMetadata(JsonElement checkoutSessionElement, string key)
        {
            if (checkoutSessionElement.TryGetProperty("metadata", out var metadataElement)
                && metadataElement.ValueKind == JsonValueKind.Object
                && metadataElement.TryGetProperty(key, out var valueElement))
            {
                return valueElement.GetString();
            }

            return null;
        }

        static string? ReadString(JsonElement element, string propertyName)
        {
            if (element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String)
            {
                return property.GetString();
            }

            return null;
        }
    }
}
