using Microsoft.AspNetCore.Mvc;
using RentARestaurant.Api.Contracts;
using RentARestaurant.Api.Infrastructure.Tenancy;
using RentARestaurant.Api.Services;

namespace RentARestaurant.Api.Controllers;

// No tenant context: a suspended tenant is inactive, so the user is resolved by membership only.
[ApiController]
[Route("api/admin/payment-recovery")]
[RequireAdminUser]
public class AdminPaymentRecoveryController(ISubscriptionService subscriptionService) : ControllerBase
{
    private string ExternalUserId => HttpContext.Request.Headers["X-Admin-User-Id"].ToString().Trim();

    [HttpGet]
    public async Task<ActionResult<PaymentRecoveryInfoResponse>> Get(CancellationToken cancellationToken)
    {
        var info = await subscriptionService.GetPaymentRecoveryInfoAsync(ExternalUserId, cancellationToken);
        return info is null ? NotFound(new { Error = "No tenant membership found for this admin user." }) : Ok(info);
    }

    [HttpPost("billing-portal")]
    public async Task<ActionResult<StripeRedirectResponse>> CreateBillingPortalSession(
        [FromBody] StripeRedirectRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await subscriptionService.CreatePaymentRecoveryPortalAsync(ExternalUserId, request.ReturnUrl, cancellationToken);
            return result is null ? NotFound(new { Error = "No tenant membership found for this admin user." }) : Ok(result);
        }
        catch (Exception ex) when (ex is SubscriptionNotLinkedException or InvalidOperationException)
        {
            return Conflict(new { Error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { Error = ex.Message });
        }
    }
}
