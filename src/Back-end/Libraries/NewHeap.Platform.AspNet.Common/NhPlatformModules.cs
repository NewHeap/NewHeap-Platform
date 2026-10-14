using Hangfire;
using Hangfire.Console;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.AspNet.Common.Services.Actors;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NewHeap.Platform.AspNet.Common.Services.Notification;
using NewHeap.Platform.Common;
using NewHeap.Platform.Common.Actors;
using NewHeap.Platform.Common.Localization;
using NewHeap.Platform.Common.Models.Options;
using NewHeap.Platform.Common.Services;
using NewHeap.Platform.Common.Utilities;
using NewHeap.Platform.Mapping;

namespace NewHeap.Platform.AspNet.Common;

/// <summary>
/// Registers background operations and notifications in an application whose EF context does not
/// derive from <see cref="NhIdentityDbContext"/>. Map the tables with
/// <see cref="NhPlatformModuleModelBuilderExtensions"/> in that context, configure the context with
/// <c>UseNewHeapSqlServer</c> or <c>UseNewHeapPostgreSql</c>, and register the platform's actor
/// interfaces for what the application knows about its users and tenancy.
/// </summary>
public static class NhPlatformModuleServiceCollectionExtensions
{
    /// <summary>
    /// Registers background operations on the tables that <typeparamref name="TContext"/> maps with
    /// <see cref="NhPlatformModuleModelBuilderExtensions.AddNewHeapBackgroundOperations"/>. An
    /// enqueue joins the transaction of <typeparamref name="TContext"/>. Register Hangfire with
    /// <see cref="AddNewHeapHangfireProcessing"/> to dispatch operations.
    /// </summary>
    public static IServiceCollection AddNewHeapBackgroundOperations<TContext>(
        this IServiceCollection services,
        Action<NhBackgroundOperationBuilder> configure)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        NhPlatformModuleRegistration.AddRepository<TContext, NhBackgroundOperation>(services);
        NhPlatformModuleRegistration.AddRepository<TContext, NhBackgroundOperationAttempt>(services);
        NhPlatformModuleRegistration.AddRepository<TContext, NhBackgroundOperationStep>(services);
        NhPlatformModuleRegistration.AddRepository<TContext, NhBackgroundOperationEvent>(services);
        NhPlatformModuleRegistration.AddRepository<TContext, NhBackgroundOperationCheckpoint>(services);
        NhPlatformModuleRegistration.AddRepository<TContext, NhBackgroundOperationIdempotencyRecord>(services);
        NhPlatformModuleRegistration.AddRepository<TContext, NhBackgroundOperationLease>(services);
        NhPlatformModuleRegistration.AddSharedInfrastructure(services);
        NhBackgroundOperationRegistration.Add(services, configure);
        return services;
    }

    /// <summary>
    /// Registers notifications and user notifications on the tables that <typeparamref name="TContext"/>
    /// maps with <see cref="NhPlatformModuleModelBuilderExtensions.AddNewHeapNotifications"/>. The
    /// module becomes the platform's <see cref="INhUserNotificationSink"/> unless the application
    /// registered its own. Entity changes are not written to the NewHeap database log. E-mail
    /// deliveries need <c>MailServiceSettings</c>, for example through <c>WithMail</c>.
    /// </summary>
    public static IServiceCollection AddNewHeapNotifications<TContext>(
        this IServiceCollection services,
        Action<NhNotificationSettings>? configure = null)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);

        NhPlatformModuleRegistration.AddRepository<TContext, NhNotification>(services);
        NhPlatformModuleRegistration.AddRepository<TContext, NhNotificationDelivery>(services);
        NhPlatformModuleRegistration.AddRepository<TContext, NhUserNotification>(services);
        NhPlatformModuleRegistration.AddRepository<TContext, NhUserNotificationMessage>(services);
        NhPlatformModuleRegistration.AddSharedInfrastructure(services);
        services.TryAddScoped<INhDbLogService, NhNoOpDbLogService>();
        // E-mail deliveries use the platform's mail service; configure MailServiceSettings to send them.
        services.AddOptions<MailServiceSettings>();
        services.TryAddScoped<NhMailService>();
        NhNotificationRegistration.Add(services, configure ?? (_ => { }));
        return services;
    }

    /// <summary>
    /// Registers Hangfire with NewHeap's process configuration and a server that serves the
    /// background-operation queues of this process.
    /// </summary>
    public static IServiceCollection AddNewHeapHangfireProcessing(
        this IServiceCollection services,
        Action<IGlobalConfiguration> hangfireOptionsAction,
        Action<ConsoleOptions>? consoleOptionsAction = null,
        Action<BackgroundJobServerOptions>? backgroundJobServerOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(hangfireOptionsAction);

        services.TryAddSingleton<INhHangfireQueueNameResolver, NhHangfireQueueNameResolver>();

        // Hangfire configuration is process-wide. This registration supports several hosts per
        // process, registers Hangfire.Console once, and keeps storage and activation per host.
        services.AddNewHeapHangfire(hangfireOptionsAction, consoleOptionsAction);

        services.AddHangfireServer((serviceProvider, options) =>
        {
            backgroundJobServerOptions?.Invoke(options);

            var queueResolver = serviceProvider.GetRequiredService<INhHangfireQueueNameResolver>();
            // The dispatcher claims operations on exactly these queues, so the server and the
            // dispatcher of one process always agree on the work this process serves.
            var backgroundOperationQueues = serviceProvider.GetService<NhBackgroundOperationServedQueues>()?.Queues
                ?? [];
            options.Queues = options.Queues
                .Append(queueResolver.GetQueueName())
                .Concat(backgroundOperationQueues)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        });

        return services;
    }

    /// <summary>
    /// Lets the platform's controllers and hubs authenticate with the application's own scheme.
    /// They require the <c>Bearer</c> and <c>Identity.Application</c> schemes of NewHeap Identity;
    /// each one that the application did not register forwards to <paramref name="scheme"/>.
    /// </summary>
    public static IServiceCollection AddNewHeapAuthenticationForwarding(
        this IServiceCollection services,
        string scheme)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(scheme);

        string[] platformSchemes = [JwtBearerDefaults.AuthenticationScheme, IdentityConstants.ApplicationScheme];
        services.AddAuthentication();
        foreach (var platformScheme in platformSchemes)
        {
            services.AddOptions<PolicySchemeOptions>(platformScheme)
                .Configure(options => options.ForwardDefault = scheme);
        }

        services.PostConfigure<AuthenticationOptions>(options =>
        {
            foreach (var platformScheme in platformSchemes)
            {
                if (!options.SchemeMap.ContainsKey(platformScheme))
                {
                    options.AddScheme<PolicySchemeHandler>(platformScheme, null);
                }
            }
        });
        return services;
    }
}

/// <summary>Maps the endpoints of platform modules registered without the NewHeap platform builder.</summary>
public static class NhPlatformModuleEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps the background-operation hub when live updates are enabled. The controllers are mapped
    /// by <c>MapControllers</c>.
    /// </summary>
    public static IEndpointRouteBuilder MapNewHeapBackgroundOperations(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        if (endpoints.ServiceProvider.GetService<NhBackgroundOperationSignalRMarker>() is not null)
        {
            var options = endpoints.ServiceProvider.GetRequiredService<NhBackgroundOperationsOptions>();
            endpoints.MapHub<NhBackgroundOperationHub>(options.HubPath);
        }

        return endpoints;
    }
}

/// <summary>The registration that NewHeap Identity hosting and standalone hosting share.</summary>
internal static class NhPlatformModuleRegistration
{
    internal static void AddRepository<TContext, TEntity>(IServiceCollection services)
        where TContext : DbContext
        where TEntity : class
    {
        services.Replace(ServiceDescriptor.Scoped<IRepository<TEntity>>(serviceProvider =>
            new Repository<TEntity>(serviceProvider.GetRequiredService<TContext>(), serviceProvider)));
    }

    /// <summary>
    /// The NewHeap services that the modules use and that the platform builder otherwise registers:
    /// mapping, localization, validation and collection processing. Existing registrations win.
    /// </summary>
    internal static void AddSharedInfrastructure(IServiceCollection services)
    {
        services.AddLogging();
        services.AddHttpContextAccessor();
        services.AddAuthorizationCore();
        services.TryAddSingleton<LogHelperService>();
        services.TryAddSingleton<ValidationService>();
        services.TryAddSingleton<IHttpCollectionProcessingService, HttpCollectionProcessingService>();
        services.TryAddSingleton<IStringLocalizerFactory, NhResourceManagerStringLocalizerFactory>();
        services.TryAddTransient(typeof(IStringLocalizer<>), typeof(StringLocalizer<>));

        if (!services.Any(descriptor => descriptor.ServiceType == typeof(NhModuleLocalizationMarker)))
        {
            services.AddSingleton<NhModuleLocalizationMarker>();
            services.Configure<NhLocalizationOptions>(options =>
            {
                options.AssemblyNameLocalizationOptions.Add(new NhLocalizationOptions.Entry
                {
                    AssemblyName = typeof(NewHeapPlatformCommonConfigurator).Assembly.GetName(),
                    Options = new LocalizationOptions { ResourcesPath = "Resources" },
                    Order = 100
                });
                options.AssemblyNameLocalizationOptions.Add(new NhLocalizationOptions.Entry
                {
                    AssemblyName = typeof(NhPlatformModuleRegistration).Assembly.GetName(),
                    Options = new LocalizationOptions { ResourcesPath = "Resources" },
                    Order = 99
                });
            });
        }

        // The modules' view models map through the platform's profiles. An application with its
        // own NewHeap mapping adds them with AddMaps(typeof(NhBackgroundOperation).Assembly).
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(IConfigurationProvider)))
        {
            services.AddAutoMapper(configuration => configuration.AddMaps(typeof(NhPlatformModuleRegistration).Assembly));
        }
    }

    /// <summary>
    /// Generic defaults of the platform's actor interfaces. NewHeap Identity and the application
    /// replace them; these only fill what nobody registered.
    /// </summary>
    internal static void AddGenericActorDefaults(IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.TryAddScoped<INhActorContextAccessor, NhClaimsActorContextAccessor>();
        services.TryAddScoped<INhDivisionAccess, NhNoDivisionAccess>();
        services.TryAddScoped<INhActorDirectory, NhIdActorDirectory>();
        services.TryAddScoped<INhUserNotificationSink, NhUserNotificationServiceSink>();
        services.TryAddScoped<INhRetention, NhRetention>();
    }

    /// <summary>NewHeap Identity's implementations, which replace the generic defaults.</summary>
    internal static void AddIdentityActorDefaults<TUser>(IServiceCollection services)
        where TUser : IdentityUser<Guid>
    {
        services.AddHttpContextAccessor();
        services.ReplaceActorDefault<INhActorContextAccessor>(typeof(NhIdentityActorContextAccessor));
        services.ReplaceActorDefault<INhDivisionAccess>(typeof(NhIdentityDivisionAccess));
        services.ReplaceActorDefault<INhActorDirectory>(typeof(NhIdentityActorDirectory<TUser>));
        services.TryAddScoped<INhUserNotificationSink, NhUserNotificationServiceSink>();
        services.TryAddScoped<INhRetention, NhRetention>();
    }

    private sealed class NhModuleLocalizationMarker
    {
    }
}

/// <summary>Registration of the background-operation module.</summary>
internal static class NhBackgroundOperationRegistration
{
    internal static NhBackgroundOperationsOptions Add(
        IServiceCollection services,
        Action<NhBackgroundOperationBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        if (services.Any(x => x.ServiceType == typeof(NhBackgroundOperationRegistry)))
        {
            throw new InvalidOperationException("Background operations have already been configured.");
        }

        var options = new NhBackgroundOperationsOptions();
        var builder = new NhBackgroundOperationBuilder(services, options);
        configure(builder);
        var registry = builder.Build();

        services.AddSingleton(options);
        services.AddSingleton(registry);
        services.AddControllers().AddApplicationPart(typeof(Controllers.NhBackgroundOperationController).Assembly);
        services.TryAddSingleton<INhHangfireQueueNameResolver, NhHangfireQueueNameResolver>();
        services.TryAddSingleton<NhBackgroundOperationServedQueues>();
        if (options.LiveUpdatesEnabled)
        {
            services.AddSignalR();
            services.TryAddSingleton<NhBackgroundOperationSignalRMarker>();
            services.TryAddSingleton<INhBackgroundOperationLiveUpdatePublisher, NhSignalRBackgroundOperationLiveUpdatePublisher>();
        }
        else
        {
            services.TryAddSingleton<INhBackgroundOperationLiveUpdatePublisher, NhNoOpBackgroundOperationLiveUpdatePublisher>();
        }
        if (options.UserNotificationProjectionEnabled)
        {
            services.TryAddScoped<INhBackgroundOperationNotificationFormatter,
                NhDefaultBackgroundOperationNotificationFormatter>();
            services.TryAddScoped<NhDefaultBackgroundOperationNotificationPolicy>();
            services.TryAddScoped<INhBackgroundOperationNotificationPolicy>(
                serviceProvider => serviceProvider.GetRequiredService<NhDefaultBackgroundOperationNotificationPolicy>());
            services.TryAddSingleton<INhBackgroundOperationNotificationProjector, NhBackgroundOperationNotificationProjector>();
        }
        else
        {
            services.TryAddSingleton<INhBackgroundOperationNotificationProjector, NhNoOpBackgroundOperationNotificationProjector>();
        }

        NhPlatformModuleRegistration.AddGenericActorDefaults(services);
        services.AddNewHeapRetentionParticipant<NhBackgroundOperationRetentionParticipant>();
        services.TryAddSingleton<NhBackgroundOperationFanOutCoordinator>();
        services.TryAddSingleton<NhBackgroundOperationPersistence>();
        services.AddHealthChecks()
            .AddCheck<NhBackgroundOperationHealthCheck>("nh-background-operations", tags: ["ready"]);
        services.TryAddScoped<INhBackgroundOperationScheduler, NhHangfireBackgroundOperationScheduler>();
        services.TryAddScoped<INhBackgroundOperationOwnerDirectory, NhActorBackgroundOperationOwnerDirectory>();
        services.TryAddScoped<INhBackgroundOperationService, NhBackgroundOperationService>();
        services.TryAddScoped<INhBackgroundOperationAdministrationService, NhBackgroundOperationService>();
        services.TryAddScoped<INhBackgroundOperationSignalService, NhBackgroundOperationSignalService>();
        services.TryAddScoped<NhBackgroundOperationOwnerSignals>();
        services.AddScoped(serviceProvider => new NhBackgroundOperationRunner(
            serviceProvider,
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            serviceProvider.GetRequiredService<NhBackgroundOperationPersistence>(),
            serviceProvider.GetRequiredService<NhBackgroundOperationRegistry>(),
            serviceProvider.GetRequiredService<NhBackgroundOperationFanOutCoordinator>(),
            serviceProvider.GetRequiredService<NhBackgroundOperationsOptions>(),
            serviceProvider.GetRequiredService<ILogger<NhBackgroundOperationRunner>>()));
        services.AddHostedService<NhBackgroundOperationStartupValidator>();
        if (options.DispatchWorkersEnabled)
        {
            services.AddHostedService<NhBackgroundOperationDispatchService>();
        }

        if (options.ReconciliationEnabled)
        {
            services.AddHostedService<NhBackgroundOperationReconciliationService>();
        }

        if (options.CleanupEnabled)
        {
            services.AddHostedService<NhBackgroundOperationCleanupService>();
        }

        return options;
    }
}

/// <summary>Registration of the notifications module.</summary>
internal static class NhNotificationRegistration
{
    internal static void Add(IServiceCollection services, Action<NhNotificationSettings> settingsAction)
    {
        ArgumentNullException.ThrowIfNull(settingsAction);

        services.Configure(settingsAction);
        services.AddScoped<INhNotificationService, NhNotificationService>();
        services.AddHostedService<NhNotificationProcessingService>();

        // Default dispatchers
        services.AddScoped<INhNotificationDispatcher, NhEmailNotificationDispatcher>();
        services.AddScoped<INhNotificationDispatcher, NhUserNotificaitonNotificationDispatcher>();
        services.Configure<NhEmailNotificationSettings>(x => { });

        // User notifications
        services.AddScoped<INhUserNotificationService, NhUserNotificationService>();

        NhPlatformModuleRegistration.AddGenericActorDefaults(services);
        services.AddNewHeapRetentionParticipant<NhNotificationRetentionParticipant>();
    }
}
