using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using NewHeap.Media.Modules;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.Media.MediaStorage.S3Bucket;

public class S3BucketStorage : IMediaStorage
{
    private readonly IOptionsSnapshot<S3MediaStorageSettings> _options;
    private readonly ILogger<S3BucketStorage> _logger;

    public S3BucketStorage(
        IOptionsSnapshot<S3MediaStorageSettings> options,
        ILogger<S3BucketStorage> logger)
    {
        _options = options;
        _logger = logger;
    }
    
    public async Task<Guid> SaveFileAsync(Stream file)
    {
        var fileName = Guid.NewGuid();
        
        using var client = CreateClient();
        await client.PutObjectAsync(new PutObjectRequest()
        {
            BucketName = _options.Value.BucketName,
            Key = fileName.ToString().ToLower(),
            InputStream = file,
            AutoCloseStream = false,
        });
        
        return fileName;
    }

    public async Task<TaskResult> UpdateFileAsync(Stream fileStream, Guid id)
    {
        try
        {
            using var client = CreateClient();
            await client.PutObjectAsync(new PutObjectRequest()
            {
                BucketName = _options.Value.BucketName,
                Key = id.ToString().ToLower(),
                InputStream = fileStream,
                AutoCloseStream = false,
            });

            return TaskResult.Succeeded();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to update media object {MediaObjectId} in S3 storage.", id);
            return TaskResult.Failed("media.storage.update-failed");
        }
    }

    public async Task<TaskResult> DeleteAsync(Guid id)
    {
        try
        {
            using var client = CreateClient();
            await client.DeleteObjectAsync(new DeleteObjectRequest()
            {
                BucketName = _options.Value.BucketName, Key = id.ToString().ToLower(),
            });
            return TaskResult.Succeeded();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to delete media object {MediaObjectId} from S3 storage.", id);
            return TaskResult.Failed("media.storage.delete-failed");
        }
    }

    public async Task<Stream?> GetFileAsync(Guid fileRefId)
    {
        try
        {
            using var utility = new TransferUtility(CreateClient());
            await using var stream = await utility.OpenStreamAsync(_options.Value.BucketName, fileRefId.ToString().ToLower());
            var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            ms.Seek(0, SeekOrigin.Begin);
            return ms;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to read media object {MediaObjectId} from S3 storage.", fileRefId);
            return null;
        }
    }

    private AmazonS3Client CreateClient()
    {
        var credentials = new BasicAWSCredentials(_options.Value.AccessKey, _options.Value.SecretKey);
        return new AmazonS3Client(credentials, _options.Value.RegionEndpoint);
    }
}
