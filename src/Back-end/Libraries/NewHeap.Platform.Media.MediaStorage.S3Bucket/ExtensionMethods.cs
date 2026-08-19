using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Media;
using NewHeap.Media.Modules;

namespace NewHeap.Platform.Media.MediaStorage.S3Bucket;

public static class ExtensionMethods
{
    public static IServiceCollection AddMediaS3BucketStorage(this IServiceCollection services, IConfiguration configuration)
    {
        return AddMediaS3BucketStorage(services, settings =>
        {
            configuration.GetSection("NewHeap:MediaLibrary:S3Settings").Bind(settings);
        });
    }
    
    public static IServiceCollection AddMediaS3BucketStorage(this IServiceCollection services, Action<S3MediaStorageSettings> configure)
    {
        services.Configure(configure);
        services.AddTransient<IMediaStorage, S3BucketStorage>();
        return services;
    }
    
    public static void UseS3BucketMediaStorage(this NhMediaServiceConfigurationContext serviceConfigurationContext, IConfiguration configuration)
    {
        serviceConfigurationContext.Services.AddMediaS3BucketStorage(configuration);
    }
    
    public static void UseS3BucketMediaStorage(this NhMediaServiceConfigurationContext serviceConfigurationContext, Action<S3MediaStorageSettings> configureSettings)
    {
        serviceConfigurationContext.Services.AddMediaS3BucketStorage(configureSettings);
    }
}