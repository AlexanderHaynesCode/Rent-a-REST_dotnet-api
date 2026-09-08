namespace RentARestaurant.Api.Domain.Entities;

public class Tenant
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string? CustomDomain { get; set; }
    public bool IsActive { get; set; } = true;
    public string SubscriptionPlan { get; set; } = "Self-Service";
    public string SubscriptionState { get; set; } = "active";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public RestaurantProfile? Profile { get; set; }
    public ICollection<TenantUser> Users { get; set; } = new List<TenantUser>();
    public ICollection<MenuCategory> MenuCategories { get; set; } = new List<MenuCategory>();
    public ICollection<MenuItem> MenuItems { get; set; } = new List<MenuItem>();
    public ICollection<BusinessHour> BusinessHours { get; set; } = new List<BusinessHour>();
    public ICollection<AgentSubmission> AgentSubmissions { get; set; } = new List<AgentSubmission>();
    public ICollection<AgentChangeAudit> AgentChangeAudits { get; set; } = new List<AgentChangeAudit>();
}
