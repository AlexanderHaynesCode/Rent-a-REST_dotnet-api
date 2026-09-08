using Microsoft.AspNetCore.Mvc;
using RentARestaurant.Api.Contracts;
using RentARestaurant.Api.Infrastructure.Tenancy;
using RentARestaurant.Api.Services;

namespace RentARestaurant.Api.Controllers;

/// <summary>
/// Internal API for agent services that need tenant metadata before tenant context is known.
/// Protected by X-Agent-Api-Key and intentionally does not require tenant context.
/// </summary>
[ApiController]
[Route("api/agent/tenants")]
[RequireInternalAgentKey]
public class AgentTenantsController(ITenantAccessService tenantAccessService) : ControllerBase
{
    [HttpPost("resolve-sender")]
    public async Task<ActionResult<ResolveTenantBySenderEmailResponse>> ResolveSender(
        [FromBody] ResolveTenantBySenderEmailRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.SenderEmail))
        {
            return BadRequest(new { Error = "SenderEmail is required." });
        }

        var resolution = await tenantAccessService.ResolveTenantBySenderEmailAsync(request.SenderEmail, cancellationToken);
        var response = new ResolveTenantBySenderEmailResponse(
            resolution.Status.ToString(),
            resolution.MatchCount,
            resolution.Tenant?.TenantId,
            resolution.Tenant?.Slug,
            resolution.Tenant?.Name,
            resolution.Tenant?.SubscriptionPlan,
            resolution.Status == SenderTenantResolutionStatus.Success);

        return Ok(response);
    }
}
