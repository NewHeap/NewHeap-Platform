using NewHeap.Media.MediaStorage.FileSystem;

// ReSharper disable once CheckNamespace
namespace NewHeap.Media;

public static class NhMediaContextExtensions
{
    public static void UseFileSystemMediaStorage(this NhMediaContext context, string storagePath)
    {
        context.Services.AddMediaFileSystemStorage(storagePath);
    }
}