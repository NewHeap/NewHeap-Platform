using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Media.Modules;

namespace NewHeap.Media.MediaStorage.FileSystem;

public static class HostBuilderExtensions
{
    public static IServiceCollection AddMediaFileSystemStorage(this IServiceCollection services, IConfiguration configuration)
    {
        return AddMediaFileSystemStorage(services,
            configuration.GetValue<string>("MediaStorage:FileSystem:StoragePath")!);
    }
    
    public static IServiceCollection AddMediaFileSystemStorage(this IServiceCollection services, string storagePath)
    {
        if (!Directory.Exists(storagePath))
        {
            throw new ArgumentException($"Storage path root {storagePath} does not exist");
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