using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NewHeap.Platform.Common.Actors;

/// <summary>
/// Registers an application's own implementations of the platform's actor interfaces. Each call
/// replaces the default that NewHeap Identity or a module registered, whatever the order.
/// </summary>
public static class NhActorServiceCollectionExtensions
{
    public static IServiceCollection AddNewHeapActorContext<TAccessor>(this IServiceCollection services)
        where TAccessor : class, INhActorContextAccessor
    {
        ArgumentNullException.ThrowIfNull(services);

        services.Replace(ServiceDescriptor.Scoped<INhActorContextAccessor, TAccessor>());
        MarkAsApplication<INhActorContextAccessor>(services);
        return services;
    }

    public static IServiceCollection AddNewHeapDivisionAccess<TAccess>(this IServiceCollection services)
        where TAccess : class, INhDivisionAccess
    {
        ArgumentNullException.ThrowIfNull(services);

        services.Replace(ServiceDescriptor.Scoped<INhDivisionAccess, TAccess>());
        MarkAsApplication<INhDivisionAccess>(services);
        return services;
    }

    public static IServiceCollection AddNewHeapActorDirectory<TDirectory>(this IServiceCollection services)
        where TDirectory : class, INhActorDirectory
    {
        ArgumentNullException.ThrowIfNull(services);

        services.Replace(ServiceDescriptor.Scoped<INhActorDirectory, TDirectory>());
        MarkAsApplication<INhActorDirectory>(services);
        return services;
    }

    public static IServiceCollection AddNewHeapActorSearch<TSearch>(this IServiceCollection services)
        where TSearch : class, INhActorSearch
    {
        ArgumentNullException.ThrowIfNull(services);

        services.Replace(ServiceDescriptor.Scoped<INhActorSearch, TSearch>());
        MarkAsApplication<INhActorSearch>(services);
        return services;
    }

    public static IServiceCollection AddNewHeapUserNotificationSink<TSink>(this IServiceCollection services)
        where TSink : class, INhUserNotificationSink
    {
        ArgumentNullException.ThrowIfNull(services);

        services.Replace(ServiceDescriptor.Scoped<INhUserNotificationSink, TSink>());
        MarkAsApplication<INhUserNotificationSink>(services);
        return services;
    }

    /// <summary>
    /// Adds a module's part of <see cref="INhRetention"/>. Registering the same participant type
    /// twice has no effect.
    /// </summary>
    public static IServiceCollection AddNewHeapRetentionParticipant<TParticipant>(this IServiceCollection services)
        where TParticipant : class, INhRetentionParticipant
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<INhRetention, NhRetention>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<INhRetentionParticipant, TParticipant>());
        return services;
    }

    /// <summary>
    /// Registers NewHeap Identity's implementation of <typeparamref name="TService"/> unless the
    /// application registered its own. It replaces a generic module default, which modules add with
    /// <c>TryAdd</c>, whatever the registration order.
    /// </summary>
    internal static IServiceCollection ReplaceActorDefault<TService>(
        this IServiceCollection services,
        Type implementationType)
        where TService : class
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(implementationType);

        if (services.Any(descriptor => descriptor.ServiceType == typeof(NhApplicationActorRegistration<TService>)))
        {
            return services;
        }

        services.Replace(ServiceDescriptor.Scoped(typeof(TService), implementationType));
        return services;
    }

    private static void MarkAsApplication<TService>(IServiceCollection services)
        where TService : class
    {
        services.TryAddSingleton<NhApplicationActorRegistration<TService>>();
    }

    /// <summary>Marks that the application registered its own <typeparamref name="TService"/>.</summary>
    private sealed class NhApplicationActorRegistration<TService>
        where TService : class
    {
    }
}
