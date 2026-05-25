using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace RentARestaurant.Api.Infrastructure.Tenancy;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequireAdminUserAttribute : Attribute, IAsyncActionFilter
{
    public Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (!context.HttpContext.Request.Headers.TryGetValue("X-Admin-User-Id", out var externalUserId)
            || string.IsNullOrWhiteSpace(externalUserId))
        {
            context.Result = new UnauthorizedObjectResult(new { Error = "Missing X-Admin-User-Id header." });
            return Task.CompletedTask;
        }

        return next();
    }
}