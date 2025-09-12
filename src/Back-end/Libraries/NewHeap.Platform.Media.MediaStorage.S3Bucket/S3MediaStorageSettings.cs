using Amazon;

namespace NewHeap.Platform.Media.MediaStorage.S3Bucket;

public class S3MediaStorageSettings
{
    public string BucketName { get; set; } = null!;
    public string AccessKey { get; set; } = null!;
    public string SecretKey { get; set; } = null!;
    public RegionEndpoint RegionEndpoint { get; set; } = RegionEndpoint.EUCentral1;
}