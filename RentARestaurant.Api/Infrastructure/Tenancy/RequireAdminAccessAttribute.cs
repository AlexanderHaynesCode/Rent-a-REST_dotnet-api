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

        if (!context.HttpContext.Request.Headers.TryGetValue("X-Admin-User-Id", out var externalUserId)
            || string.IsNullOrWhiteSpace(externalUserId))
        {
            context.Result = new UnauthorizedObjectResult(new { Error = "Missing X-Admin-User-Id header." });
            return;
        }

        var tenantAccessService = context.HttpContext.RequestServices.GetRequiredService<ITenantAccessService>();

        var allowed = await tenantAccessService.CanManageTenantAsync(
            tenantContext.TenantId.Value,
            externalUserId.ToString().Trim(),
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
