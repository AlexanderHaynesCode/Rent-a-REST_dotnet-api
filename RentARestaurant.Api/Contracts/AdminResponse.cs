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

public sealed record AdminDateSpecificHourResponse(
    DateOnly Date,
    DayOfWeek DayOfWeek,
    string OpenTime,
    string CloseTime,
    bool IsClosed);