namespace RentARestaurant.Api.Infrastructure.Agent;

public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    public string InternalApiKey { get; set; } = string.Empty;

    /// <summary>Max submissions accepted per tenant in any rolling hour (clarification replies count).</summary>
    public int MaxSubmissionsPerHour { get; set; } = 8;
}
