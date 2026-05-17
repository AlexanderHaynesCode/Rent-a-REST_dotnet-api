namespace RentARestaurant.Api.Infrastructure.Storage;

public class LocalMediaStorageOptions
{
    public const string SectionName = "LocalMediaStorage";

    public string RootPath { get; set; } = "tenant-media";
}
