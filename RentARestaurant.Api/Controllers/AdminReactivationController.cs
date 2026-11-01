using Microsoft.AspNetCore.Mvc;
using RentARestaurant.Api.Contracts;
using RentARestaurant.Api.Infrastructure.Tenancy;
using RentARestaurant.Api.Services;

namespace RentARestaurant.Api.Controllers;

// No tenant context: a canceled tenant is inactive, so the user is resolved by membership only.
[ApiController]
[Route("api/admin/reactivation")]
[RequireAdminUser]
public class AdminReactivationController(ISubscriptionService subscriptionService) : ControllerBase
{
    private string ExternalUserId => HttpContext.Request.Headers["X-Admin-User-Id"].ToString().Trim();

    [HttpGet]
    public async Task<ActionResult<ReactivationInfoResponse>> Get(CancellationToken cancellationToken)
    {
        var info = await subscriptionService.GetReactivationInfoAsync(ExternalUserId, cancellationToken);
        return info is null ? NotFound(new { Error = "No tenant membership found for this admin user." }) : Ok(info);
    }

    [HttpPost("checkout")]
    public async Task<ActionResult<StripeRedirectResponse>> CreateCheckout(
        [FromBody] ReactivationCheckoutRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await subscriptionService.CreateReactivationCheckoutAsync(
                ExternalUserId, request.Plan, request.ReturnUrl, cancellationToken);
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
