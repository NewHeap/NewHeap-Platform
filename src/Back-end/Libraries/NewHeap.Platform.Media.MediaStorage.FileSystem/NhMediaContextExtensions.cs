using Microsoft.Extensions.Configuration;
using NewHeap.Platform.Media.MediaStorage.FileSystem;

// ReSharper disable once CheckNamespace
namespace NewHeap.Media;

public static class NhMediaContextExtensions
{
    public static void UseFileSystemMediaStorage(this NhMediaServiceConfigurationContext serviceConfigurationContext, string storagePath,
        bool createDirectoryIfNotExists = false)
    {
        serviceConfigurationContext.Services.AddMediaFileSystemStorage(storagePath, createDirectoryIfNotExists);
    }

    public static void UseFileSystemMediaStorage(this NhMediaServiceConfigurationContext serviceConfigurationContext, IConfiguration configuration)
    {
        serviceConfigurationContext.Services.AddMediaFileSystemStorage(configuration);
    }
}