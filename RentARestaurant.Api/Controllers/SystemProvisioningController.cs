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
}
