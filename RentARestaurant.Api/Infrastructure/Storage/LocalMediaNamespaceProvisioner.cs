using Microsoft.Extensions.Options;

namespace RentARestaurant.Api.Infrastructure.Storage;

public class LocalMediaNamespaceProvisioner(
    IHostEnvironment hostEnvironment,
    IOptions<LocalMediaStorageOptions> options) : IMediaNamespaceProvisioner
{
    private readonly LocalMediaStorageOptions _options = options.Value;

    public Task<string> CreateTenantNamespaceAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var relative = Path.Combine(_options.RootPath, "tenants", tenantId.ToString("D"), "media");
        var absolute = Path.Combine(hostEnvironment.ContentRootPath, relative);

        Directory.CreateDirectory(absolute);  // Update this to make folder in the blob storage or w/e I'm using

        return Task.FromResult(relative.Replace("\\", "/"));
    }
}
