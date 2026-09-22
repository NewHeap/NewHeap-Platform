using AwesomeAssertions;
using Hangfire;
using Hangfire.Console;
using Hangfire.InMemory;
using Hangfire.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

[Collection(NhHangfireProcessCollection.Name)]
public sealed class NhHangfireMultiHostTests
{
    private static readonly TimeSpan JobTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task SequentialHostsStartAfterEarlierHostsAreDisposedAndKeepConsoleOutput()
    {
        foreach (var hostName in new[] { "sequential-first", "sequential-second", "sequential-third" })
        {
            using var storage = new InMemoryStorage();
            using var host = BuildHost(hostName, storage);

            await host.StartAsync();
            var result = await RunConsoleProbeAsync(host);
            await host.StopAsync();

            result.HostName.Should().Be(hostName);
            result.InjectedStorage.Should().BeSameAs(storage);
            result.ExecutingStorage.Should().BeSameAs(storage);
            result.ConsoleAttached.Should().BeTrue();
        }

        CountConsoleServerFilters().Should().Be(1);
    }

    [Fact]
    public async Task ConcurrentHostsStartTogetherAndRunJobsWithTheirOwnStorageAndServices()
    {
        using var storageA = new InMemoryStorage();
        using var storageB = new InMemoryStorage();
        using var hostA = BuildHost("concurrent-a", storageA);
        using var hostB = BuildHost("concurrent-b", storageB);

        await Task.WhenAll(hostA.StartAsync(), hostB.StartAsync());
        var results = await Task.WhenAll(RunConsoleProbeAsync(hostA), RunConsoleProbeAsync(hostB));
        await Task.WhenAll(hostA.StopAsync(), hostB.StopAsync());

        results[0].HostName.Should().Be("concurrent-a");
        results[0].InjectedStorage.Should().BeSameAs(storageA);
        results[0].ExecutingStorage.Should().BeSameAs(storageA);
        results[0].ConsoleAttached.Should().BeTrue();
        results[1].HostName.Should().Be("concurrent-b");
        results[1].InjectedStorage.Should().BeSameAs(storageB);
        results[1].ExecutingStorage.Should().BeSameAs(storageB);
        results[1].ConsoleAttached.Should().BeTrue();
        CountConsoleServerFilters().Should().Be(1);
    }

    [Fact]
    public void HostConfiguredBeforeAnotherHostKeepsItsStorageAndActivatorWhenResolvedLater()
    {
        using var storageA = new InMemoryStorage();
        using var storageB = new InMemoryStorage();
        using var providerA = BuildProvider("late-a", storageA);
        using var providerB = BuildProvider("late-b", storageB);

        providerA.GetRequiredService<IGlobalConfiguration>();
        providerB.GetRequiredService<IGlobalConfiguration>();
        var storageFromA = providerA.GetRequiredService<JobStorage>();
        var activatorFromA = providerA.GetRequiredService<JobActivator>();
        using var scope = activatorFromA.BeginScope((JobActivatorContext)null!);

        JobStorage.Current.Should().BeSameAs(storageB);
        storageFromA.Should().BeSameAs(storageA);
        ((HangfireHostMarker)scope.Resolve(typeof(HangfireHostMarker))).Name.Should().Be("late-a");
        providerA.GetRequiredService<IBackgroundJobClient>().Should()
            .NotBeSameAs(providerB.GetRequiredService<IBackgroundJobClient>());
    }

    [Fact]
    public void ConflictingConsoleOptionsFailWithAPlatformErrorThatNamesTheConflict()
    {
        using var storage = new InMemoryStorage();
        using var conflicting = BuildProvider(
            "conflicting-console",
            storage,
            console => console.PollInterval = 7_000);

        var resolve = () => conflicting.GetRequiredService<JobStorage>();

        resolve.Should().Throw<InvalidOperationException>()
            .WithMessage("*earlier host in this process*PollInterval*7000*same console options*");

        using var compatibleStorage = new InMemoryStorage();
        using var compatible = BuildProvider("compatible-console", compatibleStorage);
        compatible.GetRequiredService<JobStorage>().Should().BeSameAs(compatibleStorage);
    }

    [Fact]
    public void DirectConsoleInitializationInTheCallbackFailsWithAPlatformError()
    {
        using var storage = new InMemoryStorage();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNewHeapHangfire(
            configuration => configuration.UseStorage(storage).UseConsole(),
            consoleOptionsAction: null);
        using var provider = services.BuildServiceProvider();

        var resolve = () => provider.GetRequiredService<IGlobalConfiguration>();

        resolve.Should().Throw<InvalidOperationException>()
            .WithMessage("*initialized outside WithHangfire*consoleOptionsAction*");
    }

    [Fact]
    public void HangfireLoggingAfterAHostIsDisposedNeverUsesItsDisposedLoggerFactory()
    {
        using (var storage = new InMemoryStorage())
        {
            using var provider = BuildProvider("disposed-logging", storage);
            provider.GetRequiredService<IGlobalConfiguration>();
        }

        var log = () => Hangfire.Logging.LogProvider
            .GetLogger("NewHeap.Platform.AspNet.Common.Tests")
            .Log(Hangfire.Logging.LogLevel.Info, () => "after disposal");

        log.Should().NotThrow();
        var createStorage = () => new InMemoryStorage().Dispose();
        createStorage.Should().NotThrow();
    }

    [Fact]
    public void ExplicitlyRegisteredStorageKeepsPrecedence()
    {
        using var configuredStorage = new InMemoryStorage();
        using var explicitStorage = new InMemoryStorage();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<JobStorage>(explicitStorage);
        services.AddNewHeapHangfire(configuration => configuration.UseStorage(configuredStorage), null);
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<JobStorage>().Should().BeSameAs(explicitStorage);
    }

    private static IHost BuildHost(string hostName, JobStorage storage)
    {
        return new HostBuilder()
            .ConfigureServices(services =>
            {
                AddHostServices(services, hostName, storage, consoleOptionsAction: null);
                services.AddHangfireServer(options =>
                {
                    options.ServerName = hostName;
                    options.WorkerCount = 1;
                    options.SchedulePollingInterval = TimeSpan.FromMilliseconds(100);
                });
            })
            .Build();
    }

    private static ServiceProvider BuildProvider(
        string hostName,
        JobStorage storage,
        Action<ConsoleOptions>? consoleOptionsAction = null)
    {
        var services = new ServiceCollection();
        AddHostServices(services, hostName, storage, consoleOptionsAction);
        return services.BuildServiceProvider();
    }

    private static void AddHostServices(
        IServiceCollection services,
        string hostName,
        JobStorage storage,
        Action<ConsoleOptions>? consoleOptionsAction)
    {
        services.AddLogging();
        services.AddSingleton(new HangfireHostMarker(hostName));
        services.AddSingleton<HangfireJobProbe>();
        services.AddTransient<HangfireConsoleProbeJob>();
        services.AddNewHeapHangfire(
            configuration => configuration.UseStorage(storage),
            consoleOptionsAction);
    }

    private static async Task<HangfireProbeResult> RunConsoleProbeAsync(IHost host)
    {
        var probe = host.Services.GetRequiredService<HangfireJobProbe>();
        host.Services.GetRequiredService<IBackgroundJobClient>()
            .Enqueue<HangfireConsoleProbeJob>(job => job.Run(null));
        return await probe.Completion.Task.WaitAsync(JobTimeout);
    }

    private static int CountConsoleServerFilters()
    {
        return GlobalJobFilters.Filters.Count(filter =>
            string.Equals(
                filter.Instance.GetType().FullName,
                "Hangfire.Console.Server.ConsoleServerFilter",
                StringComparison.Ordinal));
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class NhHangfireProcessCollection : ICollectionFixture<NhHangfireProcessFixture>
{
    public const string Name = "nh-hangfire-process";
}

/// <summary>
/// Hangfire configuration is process-wide, so the first registration in this test process uses
/// the default console options, as a normal application would.
/// </summary>
public sealed class NhHangfireProcessFixture
{
    public NhHangfireProcessFixture()
    {
        using var storage = new InMemoryStorage();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNewHeapHangfire(configuration => configuration.UseStorage(storage), null);
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IGlobalConfiguration>();
    }
}

public sealed class HangfireHostMarker(string name)
{
    public string Name { get; } = name;
}

public sealed record HangfireProbeResult(
    string HostName,
    JobStorage InjectedStorage,
    JobStorage ExecutingStorage,
    bool ConsoleAttached);

public sealed class HangfireJobProbe
{
    public TaskCompletionSource<HangfireProbeResult> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public sealed class HangfireConsoleProbeJob(
    HangfireHostMarker marker,
    HangfireJobProbe probe,
    JobStorage storage)
{
    public void Run(PerformContext? context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.WriteLine($"Console output from {marker.Name}.");
        probe.Completion.TrySetResult(new HangfireProbeResult(
            marker.Name,
            storage,
            context.Storage,
            context.Items.ContainsKey("ConsoleContext")));
    }
}
