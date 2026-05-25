namespace RentARestaurant.Api.Contracts;

public sealed record AdminTenantSummaryResponse(
    Guid TenantId,
    string Slug,
    string Name,
    string? CustomDomain,
    bool IsActive,
    string SubscriptionState);

public sealed record AdminBootstrapResponse(
    AdminTenantSummaryResponse Tenant,
    PublicRestaurantResponse Restaurant);