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
    public DateOnly? Date { get; set; }

    public Tenant Tenant { get; set; } = null!;
}
