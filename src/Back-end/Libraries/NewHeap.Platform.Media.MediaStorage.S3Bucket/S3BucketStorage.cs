using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using Microsoft.Extensions.Options;
using NewHeap.Media.Modules;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.Media.MediaStorage.S3Bucket;

public class S3BucketStorage : IMediaStorage
{
    private readonly IOptionsSnapshot<S3MediaStorageSettings> _options;

    public S3BucketStorage(IOptionsSnapshot<S3MediaStorageSettings> options)
    {
        _options = options;
    }
    
    public async Task<Guid> SaveFileAsync(Stream file)
    {
        var fileName = Guid.NewGuid();
        
        var client = CreateClient();
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
            var client = CreateClient();
            await client.PutObjectAsync(new PutObjectRequest()
            {
                BucketName = _options.Value.BucketName,
                Key = id.ToString().ToLower(),
                InputStream = fileStream,
                AutoCloseStream = false,
            });

            return TaskResult.Succeeded();
        }
        catch (Exception e)
        {
            return TaskResult.Failed(e.ToString());
        }
    }

    public async Task<TaskResult> DeleteAsync(Guid id)
    {
        try
        {
            var client = CreateClient();
            var response = await client.DeleteObjectAsync(new DeleteObjectRequest()
            {
                BucketName = _options.Value.BucketName, Key = id.ToString().ToLower(),
            });
            return string.IsNullOrWhiteSpace(response.VersionId) ? TaskResult.Succeeded() : TaskResult.Failed("Could not delete file");
        }
        catch (Exception e)
        {
            return TaskResult.Failed(e.ToString());
        }
    }

    public async Task<Stream?> GetFileAsync(Guid fileRefId)
    {
        try
        {
            using var utitility = new TransferUtility(CreateClient());
            await using var stream = await utitility.OpenStreamAsync(_options.Value.BucketName, fileRefId.ToString().ToLower());
            var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            ms.Seek(0, SeekOrigin.Begin);
            return ms;
        }
        catch (Exception e)
        {
            return null;
        }
    }

    private AmazonS3Client CreateClient()
    {
        var credentials = new BasicAWSCredentials(_options.Value.AccessKey, _options.Value.SecretKey);
        return new AmazonS3Client(credentials, _options.Value.RegionEndpoint);
    }
}