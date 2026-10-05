using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RentARestaurant.Api.Contracts;
using RentARestaurant.Api.Data;
using RentARestaurant.Api.Domain.Entities;
using RentARestaurant.Api.Infrastructure.Stripe;

namespace RentARestaurant.Api.Services;

public sealed class StripeCheckoutService(
    AppDbContext dbContext,
    IHttpClientFactory httpClientFactory,
    IOptions<StripeOptions> stripeOptions,
    IOptions<MarketingOptions> marketingOptions,
    ILogger<StripeCheckoutService> logger) : IStripeCheckoutService
{
    public async Task<CreateCheckoutSessionResponse> CreateCheckoutSessionAsync(CreateCheckoutSessionRequest request, CancellationToken cancellationToken)
    {
        var normalizedPlan = request.SubscriptionPlan.Trim();
        if (normalizedPlan != SubscriptionPlans.SelfService && normalizedPlan != SubscriptionPlans.DoneForYou)
        {
            throw new ArgumentException("SubscriptionPlan must be either 'Self-Service' or 'Done-For-You'.");
        }

        if (string.IsNullOrWhiteSpace(request.OwnerExternalUserId))
        {
            throw new ArgumentException("OwnerExternalUserId is required.");
        }

        if (string.IsNullOrWhiteSpace(request.OwnerEmail))
        {
            throw new ArgumentException("OwnerEmail is required.");
        }

        var secretKey = stripeOptions.Value.SecretKey.Trim();
        if (string.IsNullOrWhiteSpace(secretKey))
        {
            throw new InvalidOperationException("Stripe secret key is not configured.");
        }

        var provisioningSession = new ProvisioningSession
        {
            Id = Guid.NewGuid(),
            Status = "pending_payment",
            RestaurantName = request.RestaurantName.Trim(),
            OwnerEmail = request.OwnerEmail.Trim(),
            OwnerExternalUserId = request.OwnerExternalUserId.Trim(),
            SubscriptionPlan = normalizedPlan,
            RequestedSlug = request.RequestedSlug?.Trim(),
            CustomDomain = request.CustomDomain?.Trim(),
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        dbContext.ProvisioningSessions.Add(provisioningSession);
        await dbContext.SaveChangesAsync(cancellationToken);

        var (sessionId, url) = await CreateStripeCheckoutSessionAsync(secretKey, provisioningSession, request, normalizedPlan, cancellationToken);
        provisioningSession.CheckoutSessionId = sessionId;
        provisioningSession.UpdatedUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Created Stripe checkout session {CheckoutSessionId} for provisioning session {ProvisioningSessionId}", sessionId, provisioningSession.Id);

        return new CreateCheckoutSessionResponse(sessionId, url);
    }

    public async Task<ProvisioningStatusResponse?> GetProvisioningStatusAsync(string checkoutSessionId, CancellationToken cancellationToken)
    {
        return await dbContext.ProvisioningSessions
            .AsNoTracking()
            .Where(x => x.CheckoutSessionId == checkoutSessionId)
            .Select(x => new ProvisioningStatusResponse(
                x.CheckoutSessionId ?? string.Empty,
                x.Status,
                x.TenantId,
                x.TenantSlug,
                x.FailureReason))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public Task<ProvisioningSession?> GetProvisioningSessionByCheckoutSessionIdAsync(string checkoutSessionId, CancellationToken cancellationToken)
    {
        return dbContext.ProvisioningSessions.FirstOrDefaultAsync(
            x => x.CheckoutSessionId == checkoutSessionId,
            cancellationToken);
    }

    public Task<ProvisioningSession?> GetProvisioningSessionByIdAsync(Guid provisioningSessionId, CancellationToken cancellationToken)
    {
        return dbContext.ProvisioningSessions.FirstOrDefaultAsync(x => x.Id == provisioningSessionId, cancellationToken);
    }

    public async Task MarkProvisioningCompletedAsync(Guid provisioningSessionId, Guid tenantId, string tenantSlug, string? stripeCustomerId, string? stripeSubscriptionId, CancellationToken cancellationToken)
    {
        var session = await dbContext.ProvisioningSessions.FirstOrDefaultAsync(x => x.Id == provisioningSessionId, cancellationToken);
        if (session is null)
        {
            return;
        }

        session.Status = "ready";
        session.TenantId = tenantId;
        session.TenantSlug = tenantSlug;
        session.StripeCustomerId = stripeCustomerId;
        session.StripeSubscriptionId = stripeSubscriptionId;
        session.FailureReason = null;
        session.UpdatedUtc = DateTime.UtcNow;

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkProvisioningProcessingAsync(Guid provisioningSessionId, CancellationToken cancellationToken)
    {
        var session = await dbContext.ProvisioningSessions.FirstOrDefaultAsync(x => x.Id == provisioningSessionId, cancellationToken);
        if (session is null)
        {
            return;
        }

        session.Status = "processing";
        session.UpdatedUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkProvisioningFailedAsync(Guid provisioningSessionId, string reason, CancellationToken cancellationToken)
    {
        var session = await dbContext.ProvisioningSessions.FirstOrDefaultAsync(x => x.Id == provisioningSessionId, cancellationToken);
        if (session is null)
        {
            return;
        }

        session.Status = "failed";
        session.FailureReason = reason;
        session.UpdatedUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<(string SessionId, string Url)> CreateStripeCheckoutSessionAsync(
        string secretKey,
        ProvisioningSession provisioningSession,
        CreateCheckoutSessionRequest request,
        string normalizedPlan,
        CancellationToken cancellationToken)
    {
        var marketingBaseUrl = marketingOptions.Value.BaseUrl.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(marketingBaseUrl))
        {
            marketingBaseUrl = "http://localhost:5173";
        }

        var subscriptionPriceId = normalizedPlan == SubscriptionPlans.DoneForYou
            ? stripeOptions.Value.DoneForYouPriceId.Trim()
            : stripeOptions.Value.SelfServicePriceId.Trim();

        if (string.IsNullOrWhiteSpace(subscriptionPriceId))
        {
            throw new InvalidOperationException($"Stripe price id is not configured for plan '{normalizedPlan}'.");
        }

        var formValues = new List<KeyValuePair<string, string>>
        {
            new("mode", "subscription"),
            new("customer_email", request.OwnerEmail.Trim()),
            new("client_reference_id", provisioningSession.Id.ToString("D")),
            new("success_url", $"{marketingBaseUrl}/signup/success?session_id={{CHECKOUT_SESSION_ID}}"),
            new("cancel_url", $"{marketingBaseUrl}/"),
            new("metadata[provisioning_session_id]", provisioningSession.Id.ToString("D")),
            new("metadata[owner_email]", request.OwnerEmail.Trim()),
            new("metadata[owner_external_user_id]", request.OwnerExternalUserId.Trim()),
            new("metadata[restaurant_name]", request.RestaurantName.Trim()),
            new("metadata[subscription_plan]", normalizedPlan),
            new("line_items[0][price]", subscriptionPriceId),
            new("line_items[0][quantity]", "1")
        };

        if (!string.IsNullOrWhiteSpace(request.RequestedSlug))
        {
            formValues.Add(new KeyValuePair<string, string>("metadata[requested_slug]", request.RequestedSlug.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(request.CustomDomain))
        {
            formValues.Add(new KeyValuePair<string, string>("metadata[custom_domain]", request.CustomDomain.Trim()));
        }

        if (normalizedPlan == SubscriptionPlans.DoneForYou)
        {
            var onboardingFeePriceId = stripeOptions.Value.DoneForYouOnboardingFeePriceId.Trim();
            if (string.IsNullOrWhiteSpace(onboardingFeePriceId))
            {
                throw new InvalidOperationException("Stripe onboarding fee price id is not configured.");
            }

            formValues.Add(new KeyValuePair<string, string>("line_items[1][price]", onboardingFeePriceId));
            formValues.Add(new KeyValuePair<string, string>("line_items[1][quantity]", "1"));
        }

        using var httpClient = httpClientFactory.CreateClient();
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "https://api.stripe.com/v1/checkout/sessions")
        {
            Content = new FormUrlEncodedContent(formValues)
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secretKey);

        using var response = await httpClient.SendAsync(httpRequest, cancellationToken);
        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogError("Stripe checkout session creation failed: {StatusCode} {Body}", response.StatusCode, responseJson);
            throw new InvalidOperationException($"Stripe checkout session creation failed: {response.StatusCode}");
        }

        using var document = JsonDocument.Parse(responseJson);
        var root = document.RootElement;
        var sessionId = root.GetProperty("id").GetString();
        var url = root.GetProperty("url").GetString();

        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(url))
        {
            throw new InvalidOperationException("Stripe checkout session response did not include a checkout URL.");
        }

        return (sessionId, url);
    }
}
