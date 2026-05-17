namespace RentARestaurant.Api.Infrastructure.Tenancy;

public class TenantResolutionOptions
{
    public const string SectionName = "TenantResolution";

    public List<string> PlatformHosts { get; set; } =
    [
        "localhost",
        "127.0.0.1"
    ];
}
