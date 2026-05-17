using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace RentARestaurant.Api.Infrastructure.Tenancy;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequireTenantContextAttribute : Attribute, IAsyncActionFilter
{
    public Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var tenantContext = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>();

        if (!tenantContext.TenantId.HasValue)
        {
            context.Result = new BadRequestObjectResult(new
            {
                Error = "Tenant context was not resolved. Use custom domain, /r/{slug}, /api/public/restaurants/{slug}, or X-Tenant-Slug header."
            });

            return Task.CompletedTask;
        }

        return next();
    }
}
