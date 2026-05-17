using RentARestaurant.Api.Domain;

namespace RentARestaurant.Api.Domain.Entities;

public class TenantUser : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string ExternalUserId { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Role { get; set; } = "Owner";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public Tenant Tenant { get; set; } = null!;
}
