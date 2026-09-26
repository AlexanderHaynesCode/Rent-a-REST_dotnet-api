using RentARestaurant.Api.Contracts;
using RentARestaurant.Api.Domain.Entities;

namespace RentARestaurant.Api.Services;

public interface IStripeCheckoutService
{
    Task<CreateCheckoutSessionResponse> CreateCheckoutSessionAsync(CreateCheckoutSessionRequest request, CancellationToken cancellationToken);

    Task<ProvisioningStatusResponse?> GetProvisioningStatusAsync(string checkoutSessionId, CancellationToken cancellationToken);

    Task<ProvisioningSession?> GetProvisioningSessionByCheckoutSessionIdAsync(string checkoutSessionId, CancellationToken cancellationToken);

    Task<ProvisioningSession?> GetProvisioningSessionByIdAsync(Guid provisioningSessionId, CancellationToken cancellationToken);

    Task MarkProvisioningCompletedAsync(Guid provisioningSessionId, Guid tenantId, string tenantSlug, string? stripeCustomerId, string? stripeSubscriptionId, CancellationToken cancellationToken);

    Task MarkProvisioningProcessingAsync(Guid provisioningSessionId, CancellationToken cancellationToken);

    Task MarkProvisioningFailedAsync(Guid provisioningSessionId, string reason, CancellationToken cancellationToken);
}
