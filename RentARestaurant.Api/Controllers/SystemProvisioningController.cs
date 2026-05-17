using Microsoft.AspNetCore.Mvc;
using RentARestaurant.Api.Contracts;
using RentARestaurant.Api.Services;

namespace RentARestaurant.Api.Controllers;

[ApiController]
[Route("api/system/provisioning")]
public class SystemProvisioningController(
    ITenantProvisioningService tenantProvisioningService,
    IWebHostEnvironment env) : ControllerBase
{
    [HttpPost("tenants")]
    public async Task<ActionResult<ProvisionTenantResponse>> ProvisionTenant([FromBody] ProvisionTenantRequest request, CancellationToken cancellationToken)
    {
        if (!env.IsDevelopment())
            return StatusCode(403, new { Error = "Tenant provisioning is restricted to the Development environment." });

        var result = await tenantProvisioningService.ProvisionTenantAsync(request, cancellationToken);
        return Ok(result);
    }
}
