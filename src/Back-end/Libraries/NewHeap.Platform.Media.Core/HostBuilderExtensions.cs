using Microsoft.Extensions.DependencyInjection;
using NewHeap.Media.Modules;

namespace NewHeap.Media;

public static class HostBuilderExtensions
{
    public static IServiceCollection AddNhMedia(this IServiceCollection services,Action<NhMediaContext>? configure)
    {
        var context = new NhMediaContext(services);
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
            context.Services.AddTransient<IAuthorizationModule, DefaultAuthorizationModule>();
        }
        
        services.AddTransient<IMediaLibraryService,MediaLibraryService>();
        return services;
    }
}