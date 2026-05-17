using RentARestaurant.Api.Domain;

namespace RentARestaurant.Api.Domain.Entities;

public class BusinessHour : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public DayOfWeek DayOfWeek { get; set; }
    public TimeOnly OpenTime { get; set; }
    public TimeOnly CloseTime { get; set; }
    public bool IsClosed { get; set; }
    // add public DateOnly? Date { get; set; } if we want to support special hours for specific dates (e.g. holidays)

    public Tenant Tenant { get; set; } = null!;
}
