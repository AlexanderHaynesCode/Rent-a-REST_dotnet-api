using RentARestaurant.Api.Contracts;

namespace RentARestaurant.Api.Services;

public interface ISubscriptionService
{
    Task<AdminSubscriptionResponse> GetCurrentAsync(CancellationToken cancellationToken);

    Task<PreviewSubscriptionChangeResponse> PreviewChangeAsync(string targetPlan, CancellationToken cancellationToken);

    Task<ChangeSubscriptionPlanResponse> ChangePlanAsync(string targetPlan, CancellationToken cancellationToken);
}
