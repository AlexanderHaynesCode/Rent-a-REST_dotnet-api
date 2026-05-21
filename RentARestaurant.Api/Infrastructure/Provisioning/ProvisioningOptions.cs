namespace RentARestaurant.Api.Infrastructure.Provisioning;

public sealed class ProvisioningOptions
{
    public const string SectionName = "Provisioning";

    public bool AllowDirectProvisioning { get; set; } = true;
}