using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.AspNet;

namespace NewHeap.Platform.AI.AspNet.Mvc;

public static class NhAiMvcBridgeServiceCollectionExtensions
{
    /// <summary>
    /// Publishes authorized MVC controller actions as governed NewHeap AI tools that execute as
    /// the calling user through the application's own HTTP pipeline. Registers the attested
    /// <see cref="NhAiMvcBridgeToolCatalog"/>, the <see cref="NhAiMvcBridgeDiscoveryPolicy"/>,
    /// the self-HTTP <see cref="INhAiMvcBridgeExecutor"/> and a startup validator, which a web host
    /// runs once its application pipeline is configured, before the server starts. Requires
    /// <c>AddNewHeapPlatformAIAspNet</c> and MVC ApiExplorer (<c>AddControllers</c> or
    /// <c>AddEndpointsApiExplorer</c>).
    /// </summary>
    public static IServiceCollection AddNewHeapPlatformAIMvcBridge(
        this IServiceCollection services,
        Action<NhAiMvcBridgeBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        if (services.Any(descriptor => descriptor.ServiceType == typeof(NhAiMvcBridgeOptions)))
        {
            throw new InvalidOperationException(
                "AddNewHeapPlatformAIMvcBridge can only be called once per application.");
        }

        var options = new NhAiMvcBridgeOptions();
        configure(new NhAiMvcBridgeBuilder(services, options));

        services.AddNewHeapPlatformAI();
        services.AddHttpContextAccessor();
        services.AddSingleton(options);
        services.AddSingleton(provider => NhAiMvcBridgeRuntimeSettings.Resolve(options, provider));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            INhAiBridgeCollectionContractProvider,
            NhAiNewHeapCollectionContractProvider>());
        services.AddSingleton<INhAiBridgeConventions>(provider =>
            (INhAiBridgeConventions)ActivatorUtilities.CreateInstance(provider, options.ConventionsType));
        services.TryAddScoped<INhAiBridgeBodySerializer, NhAiBridgeConventionBodySerializer>();
        services.AddSingleton(provider => new NhAiMvcBridgeToolCatalog(
            provider.GetRequiredService<IApiDescriptionGroupCollectionProvider>(),
            options,
            provider.GetRequiredService<NhAiMvcBridgeRuntimeSettings>(),
            provider.GetRequiredService<INhAiBridgeConventions>()));
        services.AddSingleton<INhAiToolCatalog>(provider => provider.GetRequiredService<NhAiMvcBridgeToolCatalog>());
        services.Replace(ServiceDescriptor.Scoped<INhAiToolDiscoveryPolicy, NhAiMvcBridgeDiscoveryPolicy>());
        if (options.Gateway is not null)
        {
            // Headless runs may read through the gateway with a delegated read credential.
            services.TryAddEnumerable(ServiceDescriptor.Singleton<INhAiDelegatedReadSurface, NhAiMvcBridgeReadSurface>());
        }
        services.TryAddScoped<INhAiMvcBridgeExecutor, NhAiMvcBridgeExecutor>();
        services.AddHttpClient(NhAiMvcBridgeDefaults.HttpClientName)
            .ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false
            });
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, NhAiMvcBridgeStartupValidator>());
        NhAiAspNetStartupChecks.Register(services);
        return services;
    }
}

/// <summary>
/// Builds the bridge catalog at startup and validates its attestation, so misconfiguration
/// fails before the first request. A web host builds it after its endpoints are registered, so
/// that reading ApiExplorer leaves the minimal API descriptions in place.
/// </summary>
internal sealed partial class NhAiMvcBridgeStartupValidator(
    IServiceProvider hostServices,
    NhAiMvcBridgeToolCatalog catalog,
    NhAiMvcBridgeOptions options,
    IServiceScopeFactory serviceScopeFactory,
    IEnumerable<NhAiBridgeResourcePresentationOptions> resourcePresentationOptions,
    ILogger<NhAiMvcBridgeStartupValidator> logger) : NhAiStartupCheck(hostServices)
{
    protected override Task ValidateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var scope = serviceScopeFactory.CreateScope();
        var services = scope.ServiceProvider;

        if (services.GetService<INhAiCallerCredentialAccessor>() is null)
        {
            throw new InvalidOperationException(
                "The API bridge requires AddNewHeapPlatformAIAspNet so the caller's credential can be forwarded.");
        }
        if (services.GetRequiredService<INhAiToolDiscoveryPolicy>() is not NhAiMvcBridgeDiscoveryPolicy)
        {
            throw new InvalidOperationException(
                "The API bridge discovery policy was replaced after AddNewHeapPlatformAIMvcBridge. Configure your policy with UseInnerDiscoveryPolicy instead of UseDiscoveryPolicy.");
        }

        var localizerFactory = services.GetService<IStringLocalizerFactory>();
        foreach (var presentation in resourcePresentationOptions)
        {
            if (localizerFactory is null)
            {
                throw new InvalidOperationException(
                    "AI bridge localized resource presentation requires AddLocalization.");
            }
            NhAiBridgeResourcePresentationValidator.Validate(presentation, catalog, localizerFactory);
        }

        NhAiToolCatalogAttestation.Validate(catalog, services);
        LogCatalogPublished(logger, options.ToolSetId!, catalog.Descriptors.Count);
        if (catalog.Gateway is { } gateway)
        {
            LogRelationsDerived(logger, gateway.ToolSetId, gateway.DerivedRelations);
        }
        return Task.CompletedTask;
    }

    [LoggerMessage(EventId = 10, Level = LogLevel.Information, Message = "AI API bridge '{ToolSetId}' published {ToolCount} tools.")]
    private static partial void LogCatalogPublished(ILogger logger, string toolSetId, int toolCount);

    [LoggerMessage(EventId = 11, Level = LogLevel.Information, Message = "AI API bridge gateway '{ToolSetId}' derived {RelationCount} relations between its resources.")]
    private static partial void LogRelationsDerived(ILogger logger, string toolSetId, int relationCount);
}
