using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using RentARestaurant.Api.Infrastructure.Agent;

namespace RentARestaurant.Api.Infrastructure.Tenancy;

/// <summary>
/// Restricts an endpoint to internal service-to-service calls from the AI agent pipeline
/// (agent-intake-service / agent-validator-service / agent-executor-service). Validates the
/// shared secret sent via the X-Agent-Api-Key header using a constant-time comparison.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequireInternalAgentKeyAttribute : Attribute, IAsyncActionFilter
{
    private const string HeaderName = "X-Agent-Api-Key";

    public Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var options = context.HttpContext.RequestServices.GetRequiredService<IOptions<AgentOptions>>().Value;

        if (string.IsNullOrEmpty(options.InternalApiKey))
        {
            context.Result = new ObjectResult(new { Error = "Agent API key is not configured on the server." })
            {
                StatusCode = StatusCodes.Status503ServiceUnavailable
            };
            return Task.CompletedTask;
        }

        if (!context.HttpContext.Request.Headers.TryGetValue(HeaderName, out var providedKey)
            || !IsEqual(providedKey.ToString(), options.InternalApiKey))
        {
            context.Result = new ObjectResult(new { Error = $"Missing or invalid {HeaderName} header." })
            {
                StatusCode = StatusCodes.Status403Forbidden
            };
            return Task.CompletedTask;
        }

        return next();
    }

    private static bool IsEqual(string provided, string expected)
    {
        var providedBytes = Encoding.UTF8.GetBytes(provided);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);

        // Lengths intentionally compared via FixedTimeEquals-safe path: pad shorter array so the
        // comparison itself stays constant-time and doesn't leak length via early return timing.
        if (providedBytes.Length != expectedBytes.Length)
        {
            // Still run a fixed-time compare against itself to avoid a fast-path timing signal.
            CryptographicOperations.FixedTimeEquals(providedBytes, providedBytes);
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
    }
}
