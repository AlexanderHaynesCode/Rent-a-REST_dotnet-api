using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace RentARestaurant.Api.Infrastructure.Tenancy;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequireAdminUserAttribute : Attribute, IAsyncActionFilter
{
    public Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (context.HttpContext.GetAdminUserId() is null)
        {
            context.Result = new UnauthorizedObjectResult(new { Error = "Sign-in required." });
            return Task.CompletedTask;
        }

        return next();
    }
}