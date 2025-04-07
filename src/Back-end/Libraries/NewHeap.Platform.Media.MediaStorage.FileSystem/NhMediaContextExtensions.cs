using Microsoft.Extensions.Configuration;
using NewHeap.Platform.Media.MediaStorage.FileSystem;

// ReSharper disable once CheckNamespace
namespace NewHeap.Media;

public static class NhMediaContextExtensions
{
    public static void UseFileSystemMediaStorage(this NhMediaContext context, string storagePath,
        bool createDirectoryIfNotExists = false)
    {
        context.Services.AddMediaFileSystemStorage(storagePath, createDirectoryIfNotExists);
    }

    public static void UseFileSystemMediaStorage(this NhMediaContext context, IConfiguration configuration)
    {
        context.Services.AddMediaFileSystemStorage(configuration);
    }
}