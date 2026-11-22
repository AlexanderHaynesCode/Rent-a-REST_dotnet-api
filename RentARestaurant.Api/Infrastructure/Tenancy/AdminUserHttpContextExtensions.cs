namespace RentARestaurant.Api.Infrastructure.Tenancy;

public static class AdminUserHttpContextExtensions
{
    /// <summary>
    /// The Clerk user ID from the verified session token's sub claim, or null when the request is not signed in.
    /// </summary>
    public static string? GetAdminUserId(this HttpContext httpContext)
    {
        if (httpContext.User.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var userId = httpContext.User.FindFirst("sub")?.Value?.Trim();
        return string.IsNullOrEmpty(userId) ? null : userId;
    }
}
