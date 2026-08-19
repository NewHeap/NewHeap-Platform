using Microsoft.Extensions.DependencyInjection;
using NewHeap.Media.Modules;

namespace NewHeap.Media;

public static class HostBuilderExtensions
{
    public static IServiceCollection AddNhMedia(this IServiceCollection services,Action<NhMediaServiceConfigurationContext>? configure)
    {
        var context = new NhMediaServiceConfigurationContext(services);
        services.AddSingleton(new NhMediaContext());
        if (configure != null)
        {
            configure(context);
        }

        if (!context.Services.Any(x => x.ServiceType == typeof(IFileStructureStorage)))
        {
            throw new ArgumentException("No FileStructureStorage registered");
        }
        
        if (!context.Services.Any(x => x.ServiceType == typeof(IMediaStorage)))
        {
            throw new ArgumentException("No MediaStorage registered");
        }

        if (!context.Services.Any(x => x.ServiceType == typeof(IAuthorizationModule)))
        {
            context.AddAuthentication<DefaultAuthorizationModule>();
        }

        if (!context.Services.Any(x => x.ServiceType == typeof(IThumbnailService)))
        {
            context.Services.AddTransient<IThumbnailService, ThumbnailService>();
        }
        
        services.AddTransient<IMediaLibraryService,MediaLibraryService>();
        return services;
    }
}