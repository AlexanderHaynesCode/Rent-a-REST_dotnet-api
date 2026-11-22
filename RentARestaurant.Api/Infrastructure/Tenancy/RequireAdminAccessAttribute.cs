using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using RentARestaurant.Api.Services;

namespace RentARestaurant.Api.Infrastructure.Tenancy;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequireAdminAccessAttribute : Attribute, IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var tenantContext = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>();

        if (!tenantContext.TenantId.HasValue)
        {
            context.Result = new BadRequestObjectResult(new { Error = "Tenant context missing." });
            return;
        }

        var externalUserId = context.HttpContext.GetAdminUserId();
        if (externalUserId is null)
        {
            context.Result = new UnauthorizedObjectResult(new { Error = "Sign-in required." });
            return;
        }

        var tenantAccessService = context.HttpContext.RequestServices.GetRequiredService<ITenantAccessService>();

        var allowed = await tenantAccessService.CanManageTenantAsync(
            tenantContext.TenantId.Value,
            externalUserId,
            context.HttpContext.RequestAborted);

        if (!allowed)
        {
            context.Result = new ObjectResult(new { Error = "Admin user is not authorized for this tenant." })
            {
                StatusCode = StatusCodes.Status403Forbidden
            };
            return;
        }

        await next();
    }
}
