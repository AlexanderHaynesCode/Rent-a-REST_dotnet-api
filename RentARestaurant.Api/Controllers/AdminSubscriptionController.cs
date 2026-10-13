using Microsoft.AspNetCore.Mvc;
using RentARestaurant.Api.Contracts;
using RentARestaurant.Api.Infrastructure.Tenancy;
using RentARestaurant.Api.Services;

namespace RentARestaurant.Api.Controllers;

[ApiController]
[Route("api/admin/subscription")]
public class AdminSubscriptionController(ISubscriptionService subscriptionService) : ControllerBase
{
    [HttpGet]
    [RequireTenantContext]
    [RequireAdminAccess]
    public async Task<ActionResult<AdminSubscriptionResponse>> Get(CancellationToken cancellationToken)
    {
        return Ok(await subscriptionService.GetCurrentAsync(cancellationToken));
    }

    [HttpGet("preview-change")]
    [RequireTenantContext]
    [RequireAdminAccess]
    public async Task<ActionResult<PreviewSubscriptionChangeResponse>> PreviewChange(
        [FromQuery] string targetPlan,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await subscriptionService.PreviewChangeAsync(targetPlan, cancellationToken));
        }
        catch (SubscriptionNotLinkedException ex)
        {
            return Conflict(new { Error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { Error = ex.Message });
        }
    }

    [HttpPost("change-plan")]
    [RequireTenantContext]
    [RequireAdminAccess]
    public async Task<ActionResult<ChangeSubscriptionPlanResponse>> ChangePlan(
        [FromBody] ChangeSubscriptionPlanRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await subscriptionService.ChangePlanAsync(request.TargetPlan, cancellationToken));
        }
        catch (SubscriptionNotLinkedException ex)
        {
            return Conflict(new { Error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { Error = ex.Message });
        }
    }

    [HttpPost("upgrade-checkout")]
    [RequireTenantContext]
    [RequireAdminAccess]
    public async Task<ActionResult<StripeRedirectResponse>> CreateUpgradeCheckout(
        [FromBody] StripeRedirectRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await subscriptionService.CreateUpgradeCheckoutAsync(request.ReturnUrl, cancellationToken));
        }
        catch (SubscriptionNotLinkedException ex)
        {
            return Conflict(new { Error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { Error = ex.Message });
        }
    }

    [HttpPost("billing-portal")]
    [RequireTenantContext]
    [RequireAdminAccess]
    public async Task<ActionResult<StripeRedirectResponse>> CreateBillingPortalSession(
        [FromBody] StripeRedirectRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await subscriptionService.CreateBillingPortalSessionAsync(request.ReturnUrl, cancellationToken));
        }
        catch (SubscriptionNotLinkedException ex)
        {
            return Conflict(new { Error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { Error = ex.Message });
        }
    }
}
