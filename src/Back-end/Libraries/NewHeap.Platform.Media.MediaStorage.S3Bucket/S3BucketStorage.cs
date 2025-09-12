using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using Microsoft.Extensions.Options;
using NewHeap.Media.Modules;

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
        var utility = new TransferUtility(CreateClient());
        var fileName = Guid.NewGuid();
        await utility.UploadAsync(file, _options.Value.BucketName, fileName.ToString());
        return fileName;
    }

    public async Task<bool> UpdateFileAsync(Stream fileStream, Guid id)
    {
        var client = CreateClient();
        await client.PutObjectAsync(new PutObjectRequest()
        {
            BucketName = _options.Value.BucketName,
            Key = id.ToString(),
            InputStream = fileStream,
        });

        return true;
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        var client = CreateClient();
        var response = await client.DeleteObjectAsync(new DeleteObjectRequest()
        {
            BucketName = _options.Value.BucketName, Key = id.ToString(),
        });
        return string.IsNullOrWhiteSpace(response.VersionId);
    }

    public async Task<Stream?> GetFileAsync(Guid fileRefId)
    {
        using var utitility = new TransferUtility(CreateClient());
        await using var stream = await utitility.OpenStreamAsync(_options.Value.BucketName, fileRefId.ToString());
        var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        ms.Seek(0, SeekOrigin.Begin);
        return ms;
    }

    private AmazonS3Client CreateClient()
    {
        var credentials = new BasicAWSCredentials(_options.Value.AccessKey, _options.Value.SecretKey);
        return new AmazonS3Client(credentials, _options.Value.RegionEndpoint);
    }
}