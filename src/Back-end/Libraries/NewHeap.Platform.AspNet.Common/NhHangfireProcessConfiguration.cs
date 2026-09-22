using System.Reflection;
using Hangfire;
using Hangfire.AspNetCore;
using Hangfire.Console;
using Hangfire.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NewHeap.Platform.AspNet.Common;

/// <summary>
/// Registers Hangfire so that any number of hosts can be built sequentially or concurrently in
/// one process. Hangfire applies <see cref="IGlobalConfiguration"/> to process-wide state; this
/// registration serializes that step, registers Hangfire.Console once per process, and captures
/// the storage and job activator of each host at the moment its own configuration ran.
/// </summary>
internal static class NhHangfireServiceCollectionExtensions
{
    public static IServiceCollection AddNewHeapHangfire(
        this IServiceCollection services,
        Action<IGlobalConfiguration> hangfireOptionsAction,
        Action<ConsoleOptions>? consoleOptionsAction)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(hangfireOptionsAction);

        var existingDescriptors = new HashSet<ServiceDescriptor>(services, ReferenceEqualityComparer.Instance);
        services.AddHangfire(static _ => { });
        services.AddSingleton<NhHangfireHostConfiguration>();

        // Replace only the registrations this AddHangfire call added. A storage or activator the
        // consumer registered explicitly beforehand keeps precedence, as it does in Hangfire.
        ReplaceAddedRegistration<IGlobalConfiguration>(
            services,
            existingDescriptors,
            serviceProvider => NhHangfireProcessConfiguration.Configure(
                serviceProvider,
                hangfireOptionsAction,
                consoleOptionsAction));
        ReplaceAddedRegistration<JobStorage>(
            services,
            existingDescriptors,
            serviceProvider => serviceProvider
                .GetRequiredService<NhHangfireHostConfiguration>()
                .GetStorage(serviceProvider));
        ReplaceAddedRegistration<JobActivator>(
            services,
            existingDescriptors,
            serviceProvider => serviceProvider
                .GetRequiredService<NhHangfireHostConfiguration>()
                .GetActivator(serviceProvider));

        return services;
    }

    private static void ReplaceAddedRegistration<TService>(
        IServiceCollection services,
        HashSet<ServiceDescriptor> existingDescriptors,
        Func<IServiceProvider, TService> factory)
        where TService : class
    {
        for (var index = services.Count - 1; index >= 0; index--)
        {
            var descriptor = services[index];
            if (descriptor.ServiceType != typeof(TService) || existingDescriptors.Contains(descriptor))
            {
                continue;
            }

            services[index] = ServiceDescriptor.Singleton(factory);
            return;
        }
    }
}

/// <summary>
/// The Hangfire storage and job activator of one service provider, captured while that
/// provider's configuration ran under the process-wide Hangfire configuration lock.
/// </summary>
internal sealed class NhHangfireHostConfiguration : IDisposable
{
    private JobStorage? _storage;
    private JobActivator? _activator;
    private IDisposable? _loggingRegistration;

    public void Capture(JobStorage? storage, JobActivator activator)
    {
        _storage = storage;
        _activator = activator;
    }

    public void RegisterLogging(ILoggerFactory loggerFactory)
    {
        _loggingRegistration ??= NhHangfireLogProvider.Instance.Register(loggerFactory);
    }

    public void Dispose()
    {
        // Hangfire's log provider is process-wide; a disposed host must stop receiving its logs.
        _loggingRegistration?.Dispose();
        _loggingRegistration = null;
    }

    public JobStorage GetStorage(IServiceProvider serviceProvider)
    {
        serviceProvider.GetRequiredService<IGlobalConfiguration>();

        // Without a captured storage Hangfire reports its own missing-storage error.
        return _storage ?? JobStorage.Current;
    }

    public JobActivator GetActivator(IServiceProvider serviceProvider)
    {
        serviceProvider.GetRequiredService<IGlobalConfiguration>();
        return _activator ?? JobActivator.Current;
    }
}

/// <summary>
/// The process-wide Hangfire log provider. Hangfire resolves loggers through one global provider,
/// so this provider routes each log entry to the most recently configured host that is still
/// alive and drops entries when no host is alive, instead of writing to a disposed logger factory.
/// </summary>
internal sealed class NhHangfireLogProvider : ILogProvider
{
    private readonly object _sync = new();
    private readonly List<Registration> _registrations = [];

    public static NhHangfireLogProvider Instance { get; } = new();

    public IDisposable Register(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        var registration = new Registration(this, new AspNetCoreLogProvider(loggerFactory));
        lock (_sync)
        {
            _registrations.Add(registration);
        }
        return registration;
    }

    public ILog GetLogger(string name)
    {
        return new RoutedLog(this, name);
    }

    private Registration? Current
    {
        get
        {
            lock (_sync)
            {
                return _registrations.Count == 0 ? null : _registrations[^1];
            }
        }
    }

    private void Unregister(Registration registration)
    {
        lock (_sync)
        {
            _registrations.Remove(registration);
        }
    }

    private sealed class RoutedLog(NhHangfireLogProvider owner, string name) : ILog
    {
        public bool Log(Hangfire.Logging.LogLevel logLevel, Func<string>? messageFunc, Exception? exception = null)
        {
            var registration = owner.Current;
            if (registration is null)
            {
                return false;
            }

            try
            {
                return registration.GetLogger(name).Log(logLevel, messageFunc, exception);
            }
            catch (ObjectDisposedException)
            {
                // The host was disposed between routing and writing; logging never fails a job.
                return false;
            }
        }
    }

    private sealed class Registration(NhHangfireLogProvider owner, AspNetCoreLogProvider provider) : IDisposable
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ILog> _loggers =
            new(StringComparer.Ordinal);

        public ILog GetLogger(string name)
        {
            return _loggers.GetOrAdd(name, provider.GetLogger);
        }

        public void Dispose()
        {
            owner.Unregister(this);
        }
    }
}

/// <summary>
/// Applies Hangfire configuration to Hangfire's process-wide state one host at a time.
/// </summary>
internal static class NhHangfireProcessConfiguration
{
    private const string ConsoleAlreadyInitializedMessage = "Console is already initialized";

    private static readonly object Gate = new();
    private static readonly PropertyInfo[] ConsoleOptionProperties = typeof(ConsoleOptions)
        .GetProperties(BindingFlags.Instance | BindingFlags.Public)
        .Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
        .OrderBy(property => property.Name, StringComparer.Ordinal)
        .ToArray();

    private static IReadOnlyList<KeyValuePair<string, object?>>? _registeredConsoleOptions;

    public static IGlobalConfiguration Configure(
        IServiceProvider serviceProvider,
        Action<IGlobalConfiguration> hangfireOptionsAction,
        Action<ConsoleOptions>? consoleOptionsAction)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(hangfireOptionsAction);

        var hostConfiguration = serviceProvider.GetRequiredService<NhHangfireHostConfiguration>();
        var loggerFactory = serviceProvider.GetService<ILoggerFactory>();
        var scopeFactory = serviceProvider.GetService<IServiceScopeFactory>();
        var consoleOptions = new ConsoleOptions();
        consoleOptionsAction?.Invoke(consoleOptions);

        lock (Gate)
        {
            var configuration = GlobalConfiguration.Configuration;

            // The same defaults Hangfire.NetCore applies; the consumer callback may replace them.
            // Logging routes through one process-wide provider to a live host, because Hangfire
            // components keep using the global provider after the host that set it is disposed.
            if (loggerFactory is not null)
            {
                hostConfiguration.RegisterLogging(loggerFactory);
                configuration.UseLogProvider(NhHangfireLogProvider.Instance);
            }
            if (scopeFactory is not null)
            {
                configuration.UseActivator(new AspNetCoreJobActivator(scopeFactory));
            }

            try
            {
                hangfireOptionsAction(configuration);
            }
            catch (InvalidOperationException exception) when (IsConsoleAlreadyInitialized(exception))
            {
                throw CreateExternalConsoleException(exception);
            }

            EnsureConsole(configuration, consoleOptions);
            hostConfiguration.Capture(TryGetCurrentStorage(), JobActivator.Current);
            return configuration;
        }
    }

    private static void EnsureConsole(IGlobalConfiguration configuration, ConsoleOptions options)
    {
        var requested = SnapshotConsoleOptions(options);
        if (_registeredConsoleOptions is null)
        {
            try
            {
                configuration.UseConsole(options);
            }
            catch (InvalidOperationException exception) when (IsConsoleAlreadyInitialized(exception))
            {
                throw CreateExternalConsoleException(exception);
            }

            _registeredConsoleOptions = requested;
            return;
        }

        var conflicts = _registeredConsoleOptions
            .Zip(requested, (registered, current) => (Registered: registered, Current: current))
            .Where(pair => !Equals(pair.Registered.Value, pair.Current.Value))
            .Select(pair =>
                $"{pair.Registered.Key} is '{pair.Registered.Value}' but this host requested '{pair.Current.Value}'")
            .ToArray();
        if (conflicts.Length > 0)
        {
            throw new InvalidOperationException(
                "WithHangfire cannot apply the requested Hangfire.Console options because an earlier host in this "
                + "process already registered different options: "
                + string.Join("; ", conflicts)
                + ". Hangfire.Console options are process-wide; use the same console options for every host in the process.");
        }
    }

    private static IReadOnlyList<KeyValuePair<string, object?>> SnapshotConsoleOptions(ConsoleOptions options)
    {
        return ConsoleOptionProperties
            .Select(property => new KeyValuePair<string, object?>(property.Name, property.GetValue(options)))
            .ToArray();
    }

    private static JobStorage? TryGetCurrentStorage()
    {
        try
        {
            return JobStorage.Current;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static bool IsConsoleAlreadyInitialized(InvalidOperationException exception)
    {
        return string.Equals(exception.Message, ConsoleAlreadyInitializedMessage, StringComparison.Ordinal)
            && exception.TargetSite?.Module.Assembly == typeof(ConsoleOptions).Assembly;
    }

    private static InvalidOperationException CreateExternalConsoleException(Exception innerException)
    {
        return new InvalidOperationException(
            "Hangfire.Console was initialized outside WithHangfire in this process. Remove the direct UseConsole "
            + "call and pass console settings through the WithHangfire consoleOptionsAction; WithHangfire registers "
            + "console support once per process and reuses it for every host.",
            innerException);
    }
}
