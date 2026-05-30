using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;

namespace RentARestaurant.Api.Infrastructure.Storage;

public class CloudflareR2StorageService : IR2StorageService
{
    private readonly CloudflareR2Options _options;
    private readonly AmazonS3Client _s3Client;

    public CloudflareR2StorageService(IOptions<CloudflareR2Options> options)
    {
        _options = options.Value;

        var credentials = new BasicAWSCredentials(_options.AccessKeyId, _options.SecretAccessKey);
        var config = new AmazonS3Config
        {
            ServiceURL = $"https://{_options.AccountId}.r2.cloudflarestorage.com",
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED
        };

        _s3Client = new AmazonS3Client(credentials, config);
    }

    public async Task<string> UploadImageAsync(
        Guid tenantId,
        string imageType,
        Stream stream,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        var key = $"tenants/{tenantId:D}/{imageType}";

        var request = new PutObjectRequest
        {
            BucketName = _options.BucketName,
            Key = key,
            InputStream = stream,
            ContentType = contentType,
            DisablePayloadSigning = true
        };

        await _s3Client.PutObjectAsync(request, cancellationToken);

        return $"{_options.PublicBaseUrl.TrimEnd('/')}/tenants/{tenantId:D}/{imageType}";
    }
}
