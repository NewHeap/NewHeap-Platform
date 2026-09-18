using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.AspNet;

namespace NewHeap.Platform.AI.AspNet.Mvc;

public static class NhAiMvcBridgeServiceCollectionExtensions
{
    /// <summary>
    /// Publishes authorized MVC controller actions as governed NewHeap AI tools that execute as
    /// the calling user through the application's own HTTP pipeline. Registers the attested
    /// <see cref="NhAiMvcBridgeToolCatalog"/>, the <see cref="NhAiMvcBridgeDiscoveryPolicy"/>,
    /// the self-HTTP <see cref="INhAiMvcBridgeExecutor"/> and a startup validator. Requires
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
        services.AddSingleton<INhAiBridgeConventions>(provider =>
            (INhAiBridgeConventions)ActivatorUtilities.CreateInstance(provider, options.ConventionsType));
        services.AddSingleton(provider => new NhAiMvcBridgeToolCatalog(
            provider.GetRequiredService<IApiDescriptionGroupCollectionProvider>(),
            options,
            provider.GetRequiredService<INhAiBridgeConventions>()));
        services.AddSingleton<INhAiToolCatalog>(provider => provider.GetRequiredService<NhAiMvcBridgeToolCatalog>());
        services.Replace(ServiceDescriptor.Scoped<INhAiToolDiscoveryPolicy, NhAiMvcBridgeDiscoveryPolicy>());
        services.TryAddScoped<INhAiMvcBridgeExecutor, NhAiMvcBridgeExecutor>();
        services.AddHttpClient(NhAiMvcBridgeDefaults.HttpClientName)
            .ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false
            });
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, NhAiMvcBridgeStartupValidator>());
        return services;
    }
}

/// <summary>
/// Builds the bridge catalog at startup and validates its attestation, so misconfiguration
/// fails before the first request.
/// </summary>
internal sealed partial class NhAiMvcBridgeStartupValidator(
    NhAiMvcBridgeToolCatalog catalog,
    NhAiMvcBridgeOptions options,
    IServiceScopeFactory serviceScopeFactory,
    ILogger<NhAiMvcBridgeStartupValidator> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
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

        NhAiToolCatalogAttestation.Validate(catalog, services);
        LogCatalogPublished(logger, options.ToolSetId!, catalog.Descriptors.Count);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    [LoggerMessage(EventId = 10, Level = LogLevel.Information, Message = "AI API bridge '{ToolSetId}' published {ToolCount} tools.")]
    private static partial void LogCatalogPublished(ILogger logger, string toolSetId, int toolCount);
}
