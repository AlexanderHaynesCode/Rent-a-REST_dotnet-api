namespace RentARestaurant.Api.Infrastructure.Storage;

public interface IR2StorageService
{
    Task<string> UploadImageAsync(
        Guid tenantId,
        string imageType,
        Stream stream,
        string contentType,
        CancellationToken cancellationToken = default);
}
