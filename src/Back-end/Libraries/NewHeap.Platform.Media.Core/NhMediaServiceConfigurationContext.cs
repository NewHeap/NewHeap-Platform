using Microsoft.Extensions.DependencyInjection;
using NewHeap.Media.EventHandlers;
using NewHeap.Media.Modules;

namespace NewHeap.Media;

public class NhMediaServiceConfigurationContext
{
    public IServiceCollection Services { get; }

    internal NhMediaServiceConfigurationContext(IServiceCollection services)
    {
        Services = services;
    }

    public NhMediaServiceConfigurationContext AddThumbnailService<TService>() where TService : class, IThumbnailService
    {
        Services.AddTransient<IThumbnailService, TService>();

        if (typeof(TService).IsAssignableTo(typeof(IHandleMediaLibraryEvent)))
        {
            Services.AddTransient(typeof(IHandleMediaLibraryEvent), typeof(TService));
        }
        
        return this;
    }
    
    public NhMediaServiceConfigurationContext AddEventHandler<THandler>() where THandler : class, IHandleMediaLibraryEvent
    {
        Services.AddTransient<IHandleMediaLibraryEvent, THandler>();
        return this;
    }
    
    public NhMediaServiceConfigurationContext AddStorage<TStorage>() 
        where TStorage : class, IMediaStorage
    {
        Services.AddTransient<IMediaStorage, TStorage>();
        return this;
    }
    
    public NhMediaServiceConfigurationContext AddFileStructureStorage<TStorage>() 
        where TStorage : class, IFileStructureStorage
    {
        Services.AddTransient<IFileStructureStorage, TStorage>();
        return this;
    }
    
    public NhMediaServiceConfigurationContext AddAuthentication<TAuth>() 
        where TAuth : class, IAuthorizationModule
    {
        Services.AddTransient<IAuthorizationModule, TAuth>();
        return this;
    }
}