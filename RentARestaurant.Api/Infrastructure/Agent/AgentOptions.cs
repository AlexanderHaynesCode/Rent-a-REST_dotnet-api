namespace RentARestaurant.Api.Infrastructure.Agent;

public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    public string InternalApiKey { get; set; } = string.Empty;
}
