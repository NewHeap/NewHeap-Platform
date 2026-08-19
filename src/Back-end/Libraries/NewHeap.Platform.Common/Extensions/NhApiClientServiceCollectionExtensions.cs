using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.Common.Models.Options;
using NewHeap.Platform.Common.Services.Api;

namespace NewHeap.Platform.Common;

public static partial class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds a reusable client for one logical target API.
    /// </summary>
    public static IHttpClientBuilder AddNhApiClient<TApi>(
        this IServiceCollection services,
        Action<NhApiClientOptions> configure)
        where TApi : class
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new NhApiClientOptions();
        configure(options);
        options.Validate();

        return AddNhApiClient<TApi>(services, options, useCustomAccessTokenProvider: false);
    }

    /// <summary>
    /// Adds a reusable client and binds its options from configuration.
    /// </summary>
    public static IHttpClientBuilder AddNhApiClient<TApi>(
        this IServiceCollection services,
        IConfiguration configuration)
        where TApi : class
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return services.AddNhApiClient<TApi>(options => configuration.Bind(options));
    }

    /// <summary>
    /// Adds a reusable client with a consumer-provided bearer token provider.
    /// </summary>
    public static IHttpClientBuilder AddNhApiClient<TApi, TAccessTokenProvider>(
        this IServiceCollection services,
        Action<NhApiClientOptions> configure)
        where TApi : class
        where TAccessTokenProvider : class, INhApiAccessTokenProvider<TApi>
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new NhApiClientOptions();
        configure(options);
        options.Validate();

        services.TryAddSingleton<INhApiAccessTokenProvider<TApi>, TAccessTokenProvider>();
        return AddNhApiClient<TApi>(services, options, useCustomAccessTokenProvider: true);
    }

    private static IHttpClientBuilder AddNhApiClient<TApi>(
        IServiceCollection services,
        NhApiClientOptions options,
        bool useCustomAccessTokenProvider)
        where TApi : class
    {
        if (services.Any(descriptor =>
                descriptor.ServiceType == typeof(INhApiHttpClientFactory<TApi>)))
        {
            throw new InvalidOperationException(
                $"An API client for {typeof(TApi).FullName} has already been registered.");
        }

        services.AddSingleton(new NhApiClientRegistration<TApi>(options));
        services.AddSingleton<INhApiHttpClientFactory<TApi>, NhApiHttpClientFactory<TApi>>();

        var authenticationEnabled = useCustomAccessTokenProvider || options.Authentication != null;
        if (options.Authentication != null && !useCustomAccessTokenProvider)
        {
            services.AddSingleton<
                INhApiAccessTokenProvider<TApi>,
                NhUsernamePasswordApiAccessTokenProvider<TApi>>();
        }

        if (authenticationEnabled)
        {
            services.AddTransient<NhApiAuthenticationDelegatingHandler<TApi>>();
        }

        services
            .AddHttpClient(
                NhApiClientNames.GetAuthenticationClientName<TApi>(),
                httpClient =>
                {
                    httpClient.BaseAddress = options.BaseAddress;
                    httpClient.Timeout = options.Timeout;
                });

        var httpClientBuilder = services.AddHttpClient(
            NhApiClientNames.GetClientName<TApi>(),
            httpClient =>
            {
                httpClient.BaseAddress = options.BaseAddress;
                httpClient.Timeout = options.Timeout;
            });

        if (authenticationEnabled)
        {
            httpClientBuilder.AddHttpMessageHandler<NhApiAuthenticationDelegatingHandler<TApi>>();
        }

        return httpClientBuilder;
    }
}
