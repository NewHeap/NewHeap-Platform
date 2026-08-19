using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Media.Modules;

namespace NewHeap.Platform.Media.MediaStorage.FileSystem;

public static class HostBuilderExtensions
{
    public static IServiceCollection AddMediaFileSystemStorage(this IServiceCollection services, IConfiguration configuration)
    {
        return AddMediaFileSystemStorage(services,
            configuration.GetValue<string>("NewHeap:MediaLibrary:RootDirectory")!, true);
    }
    
    public static IServiceCollection AddMediaFileSystemStorage(this IServiceCollection services, string storagePath, bool createIfNotExists = false)
    {
        if (!Directory.Exists(storagePath) && !createIfNotExists)
        {
            throw new ArgumentException($"Storage path root {storagePath} does not exist");
        }

        if (!Directory.Exists(storagePath))
        {
            Directory.CreateDirectory(storagePath);
        }
        return AddMediaFileSystemStorage(services, x =>
        {
            x.StoragePath = storagePath;
        });
    }

    public static IServiceCollection AddMediaFileSystemStorage(this IServiceCollection services,Action<DefaultMediaStorageSettings> configure)
    {
        services.Configure(configure);
        services.AddTransient<IMediaStorage, DefaultMediaStorage>();
        return services;
    }
}