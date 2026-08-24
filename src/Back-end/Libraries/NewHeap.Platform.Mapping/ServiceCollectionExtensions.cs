using NewHeap.Platform.Mapping;
using System.Reflection;

namespace Microsoft.Extensions.DependencyInjection;

public static class NewHeapMappingServiceCollectionExtensions
{
    public static IServiceCollection AddAutoMapper(
        this IServiceCollection services,
        Action<IMapperConfigurationExpression> configurationAction)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configurationAction);

        services.AddSingleton<IConfigurationProvider>(_ =>
            new MapperConfiguration(configurationAction));
        services.AddTransient<IMapper>(serviceProvider =>
            new Mapper(
                serviceProvider.GetRequiredService<IConfigurationProvider>(),
                serviceProvider.GetService));

        return services;
    }

    public static IServiceCollection AddAutoMapper(
        this IServiceCollection services,
        Action<IMapperConfigurationExpression> configurationAction,
        params Assembly[] assemblies)
        => services.AddAutoMapper(configuration =>
        {
            configurationAction(configuration);
            configuration.AddMaps(assemblies);
        });

    public static IServiceCollection AddAutoMapper(
        this IServiceCollection services,
        Action<IMapperConfigurationExpression> configurationAction,
        params Type[] markerTypes)
        => services.AddAutoMapper(configuration =>
        {
            configurationAction(configuration);
            configuration.AddMaps(markerTypes);
        });
}
