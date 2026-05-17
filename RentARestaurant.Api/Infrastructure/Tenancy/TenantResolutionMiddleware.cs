using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RentARestaurant.Api.Data;

namespace RentARestaurant.Api.Infrastructure.Tenancy;

public class TenantResolutionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext httpContext,
        ITenantContext tenantContext,
        AppDbContext dbContext,
        IOptions<TenantResolutionOptions> options)
    {
        if (IsSystemRoute(httpContext.Request.Path))
        {
            tenantContext.MarkSystemRequest();
            await next(httpContext);
            return;
        }

        var host = httpContext.Request.Host.Host.ToLowerInvariant();
        string? slug = null;

        if (httpContext.Request.Headers.TryGetValue("X-Tenant-Slug", out var headerSlug))
        {
            slug = headerSlug.ToString().Trim().ToLowerInvariant();
        }

        if (string.IsNullOrWhiteSpace(slug))
        {
            slug = TryGetSlugFromPath(httpContext.Request.Path);
        }

        if (string.IsNullOrWhiteSpace(slug) && ShouldAttemptCustomDomainLookup(host, options.Value))
        {
            var tenantByDomain = await dbContext.Tenants
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.CustomDomain != null && x.CustomDomain.ToLower() == host);

            if (tenantByDomain is not null)
            {
                tenantContext.SetTenant(tenantByDomain.Id, tenantByDomain.Slug);
                await next(httpContext);
                return;
            }
        }

        if (!string.IsNullOrWhiteSpace(slug))
        {
            var tenant = await dbContext.Tenants
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Slug == slug && x.IsActive);

            if (tenant is not null)
            {
                tenantContext.SetTenant(tenant.Id, tenant.Slug);
            }
        }

        await next(httpContext);
    }

    private static bool IsSystemRoute(PathString path)
    {
        return path.StartsWithSegments("/api/system")
               || path.StartsWithSegments("/api/stripe/webhooks")
               || path.StartsWithSegments("/health")
               || path.StartsWithSegments("/openapi")
               || path.StartsWithSegments("/swagger");
    }

    private static bool ShouldAttemptCustomDomainLookup(string host, TenantResolutionOptions options)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        return !options.PlatformHosts.Any(x => string.Equals(x, host, StringComparison.OrdinalIgnoreCase));
    }

    private static string? TryGetSlugFromPath(PathString path)
    {
        var value = path.Value;
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (segments.Length >= 2 && string.Equals(segments[0], "r", StringComparison.OrdinalIgnoreCase))
        {
            return segments[1].ToLowerInvariant();
        }

        if (segments.Length >= 4
            && string.Equals(segments[0], "api", StringComparison.OrdinalIgnoreCase)
            && string.Equals(segments[1], "public", StringComparison.OrdinalIgnoreCase)
            && string.Equals(segments[2], "restaurants", StringComparison.OrdinalIgnoreCase))
        {
            return segments[3].ToLowerInvariant();
        }

        return null;
    }
}
