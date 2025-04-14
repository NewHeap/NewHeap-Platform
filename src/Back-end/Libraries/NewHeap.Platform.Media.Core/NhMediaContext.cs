using Microsoft.Extensions.DependencyInjection;
using NewHeap.Media.EventHandlers;
using NewHeap.Media.Modules;

namespace NewHeap.Media;

public class NhMediaContext
{
    public IServiceCollection Services { get; }

    internal NhMediaContext(IServiceCollection services)
    {
        Services = services;
    }

    public NhMediaContext AddEventHandler<THandler>() where THandler : class, IHandleMediaLibraryEvent
    {
        Services.AddTransient<IHandleMediaLibraryEvent, THandler>();
        return this;
    }
    
    public NhMediaContext AddStorage<TStorage>() 
        where TStorage : class, IMediaStorage
    {
        Services.AddTransient<IMediaStorage, TStorage>();
        return this;
    }
    
    public NhMediaContext AddFileStructureStorage<TStorage>() 
        where TStorage : class, IFileStructureStorage
    {
        Services.AddTransient<IFileStructureStorage, TStorage>();
        return this;
    }
    
    public NhMediaContext AddAuthentication<TAuth>() 
        where TAuth : class, IAuthorizationModule
    {
        Services.AddTransient<IAuthorizationModule, TAuth>();
        return this;
    }
}