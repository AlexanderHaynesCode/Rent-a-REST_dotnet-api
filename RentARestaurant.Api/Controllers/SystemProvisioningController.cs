using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using RentARestaurant.Api.Contracts;
using RentARestaurant.Api.Infrastructure.Provisioning;
using RentARestaurant.Api.Services;

namespace RentARestaurant.Api.Controllers;

[ApiController]
[Route("api/system/provisioning")]
public class SystemProvisioningController(
    ITenantProvisioningService tenantProvisioningService,
    IOptions<ProvisioningOptions> provisioningOptions,
    IStripeCheckoutService stripeCheckoutService,
    IWebHostEnvironment environment,
    ILogger<SystemProvisioningController> logger) : ControllerBase
{
    [HttpPost("tenants")]
    public async Task<ActionResult<ProvisionTenantResponse>> ProvisionTenant([FromBody] ProvisionTenantRequest request, CancellationToken cancellationToken)
    {
        if (!provisioningOptions.Value.AllowDirectProvisioning)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                Error = "Direct tenant provisioning is currently disabled."
            });
        }

        try
        {
            var result = await tenantProvisioningService.ProvisionTenantAsync(request, cancellationToken);
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { Error = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled error while provisioning tenant for owner {OwnerExternalUserId}", request.OwnerExternalUserId);
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                Error = "Failed to provision tenant due to an internal server error."
            });
        }
    }

    [HttpPost("checkout-sessions")]
    public async Task<ActionResult<CreateCheckoutSessionResponse>> CreateCheckoutSession([FromBody] CreateCheckoutSessionRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await stripeCheckoutService.CreateCheckoutSessionAsync(request, cancellationToken);
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { Error = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create Stripe checkout session for {OwnerExternalUserId}", request.OwnerExternalUserId);
            return StatusCode(StatusCodes.Status500InternalServerError, new { Error = "Failed to create checkout session." });
        }
    }

    [HttpGet("status")]
    public async Task<ActionResult<ProvisioningStatusResponse>> GetStatus([FromQuery] string sessionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return BadRequest(new { Error = "sessionId is required." });
        }

        var status = await stripeCheckoutService.GetProvisioningStatusAsync(sessionId.Trim(), cancellationToken);
        if (status is null)
        {
            return NotFound(new { Error = "Provisioning session not found." });
        }

        return Ok(status);
    }

    [HttpPost("recover")]
    public async Task<ActionResult<ProvisioningStatusResponse>> Recover([FromQuery] string sessionId, CancellationToken cancellationToken)
    {
        if (!environment.IsDevelopment())
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                Error = "Stripe provisioning recovery is only available in development."
            });
        }

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return BadRequest(new { Error = "sessionId is required." });
        }

        var checkoutSessionId = sessionId.Trim();
        var session = await stripeCheckoutService.GetProvisioningSessionByCheckoutSessionIdAsync(checkoutSessionId, cancellationToken);
        if (session is null)
        {
            return NotFound(new { Error = "Provisioning session not found." });
        }

        if (string.Equals(session.Status, "ready", StringComparison.OrdinalIgnoreCase) && session.TenantId.HasValue)
        {
            return Ok(new ProvisioningStatusResponse(
                session.CheckoutSessionId ?? checkoutSessionId,
                session.Status,
                session.TenantId,
                session.TenantSlug,
                session.FailureReason));
        }

        if (string.Equals(session.Status, "failed", StringComparison.OrdinalIgnoreCase))
        {
            return Ok(new ProvisioningStatusResponse(
                session.CheckoutSessionId ?? checkoutSessionId,
                session.Status,
                session.TenantId,
                session.TenantSlug,
                session.FailureReason));
        }

        await stripeCheckoutService.MarkProvisioningProcessingAsync(session.Id, cancellationToken);

        var checkoutCompleted = new ProvisionTenantRequest(
            session.RestaurantName,
            session.OwnerEmail,
            session.OwnerExternalUserId,
            session.SubscriptionPlan,
            session.RequestedSlug,
            session.StripeCustomerId,
            session.StripeSubscriptionId,
            session.CustomDomain);

        try
        {
            var result = await tenantProvisioningService.ProvisionTenantAsync(checkoutCompleted, cancellationToken);

            await stripeCheckoutService.MarkProvisioningCompletedAsync(
                session.Id,
                result.TenantId,
                result.Slug,
                session.StripeCustomerId,
                session.StripeSubscriptionId,
                cancellationToken);

            return Ok(new ProvisioningStatusResponse(
                session.CheckoutSessionId ?? checkoutSessionId,
                "ready",
                result.TenantId,
                result.Slug,
                null));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to recover tenant provisioning from Stripe checkout session {CheckoutSessionId}", checkoutSessionId);
            await stripeCheckoutService.MarkProvisioningFailedAsync(session.Id, ex.Message, cancellationToken);
            return StatusCode(StatusCodes.Status500InternalServerError, new { Error = "Failed to recover tenant provisioning." });
        }
    }
}
