using Hangfire;
using Hangfire.Console;
using Hangfire.InMemory;
using Hangfire.Server;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NewHeap.Platform.AspNet.Common;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models.Mutate;
using NewHeap.Platform.AspNet.Common.Models.Options;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.AspNet.Common.Utilities;
using SampleProjectManagement.DAL;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

public sealed class HangfireProcessSamplesTests
{
    private static readonly TimeSpan JobTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task WithHangfire_supports_sequential_and_concurrent_hosts_in_one_process()
    {
        // A test run, a worker restart or an integration fixture may build several hosts in one
        // process. Each host keeps its own storage and services and still writes job console output.
        foreach (var hostName in new[] { "sample-sequential-1", "sample-sequential-2" })
        {
            await using var host = SampleHangfireHost.Create(hostName);
            await host.StartAsync();

            var result = await host.RunConsoleJobAsync();
            await host.StopAsync();

            Assert.Equal(hostName, result.HostName);
            Assert.Same(host.Storage, result.Storage);
            Assert.True(result.ConsoleAttached);
        }

        await using var first = SampleHangfireHost.Create("sample-concurrent-1");
        await using var second = SampleHangfireHost.Create("sample-concurrent-2");
        await Task.WhenAll(first.StartAsync(), second.StartAsync());

        var results = await Task.WhenAll(first.RunConsoleJobAsync(), second.RunConsoleJobAsync());
        await Task.WhenAll(first.StopAsync(), second.StopAsync());

        Assert.Equal("sample-concurrent-1", results[0].HostName);
        Assert.Same(first.Storage, results[0].Storage);
        Assert.Equal("sample-concurrent-2", results[1].HostName);
        Assert.Same(second.Storage, results[1].Storage);
        Assert.All(results, result => Assert.True(result.ConsoleAttached));
    }

    [Fact]
    public void WithHangfire_rejects_console_options_that_conflict_with_an_earlier_host()
    {
        using var defaultHost = SampleHangfireHost.CreateProvider("sample-default-console", out _);
        defaultHost.GetRequiredService<JobStorage>();

        using var conflictingHost = SampleHangfireHost.CreateProvider(
            "sample-conflicting-console",
            out _,
            console => console.ExpireIn = TimeSpan.FromHours(2));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            conflictingHost.GetRequiredService<JobStorage>());
        Assert.Contains("ExpireIn", exception.Message, StringComparison.Ordinal);
        Assert.Contains("same console options", exception.Message, StringComparison.Ordinal);
    }

    private sealed class SampleHangfireHost : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly IReadOnlyList<IHostedService> _hangfireServers;

        private SampleHangfireHost(ServiceProvider provider, InMemoryStorage storage)
        {
            _provider = provider;
            Storage = storage;
            _hangfireServers = provider.GetServices<IHostedService>()
                .Where(service => service.GetType().Namespace?.StartsWith("Hangfire", StringComparison.Ordinal) == true)
                .ToArray();
        }

        public InMemoryStorage Storage { get; }

        public static SampleHangfireHost Create(string hostName)
        {
            var provider = CreateProvider(hostName, out var storage);
            return new SampleHangfireHost(provider, storage);
        }

        public static ServiceProvider CreateProvider(
            string hostName,
            out InMemoryStorage storage,
            Action<ConsoleOptions>? consoleOptionsAction = null)
        {
            var hostStorage = new InMemoryStorage();
            storage = hostStorage;
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["NewHeap:PlatformAspNetCommon:Authorization:JWT:Token:Issuer"] = "sample-project-management",
                    ["NewHeap:PlatformAspNetCommon:Authorization:JWT:Token:Key"] = "sample-project-management-signing-key-2026"
                })
                .Build();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IConfiguration>(configuration);
            services.AddSingleton(new SampleHangfireHostMarker(hostName));
            services.AddSingleton<SampleHangfireJobProbe>();
            services.AddTransient<SampleHangfireConsoleJob>();
            services
                .AddNewHeapPlatformAspNetCommon<
                    NhUser,
                    NhUserRole,
                    NhDivision,
                    NhDivisionUser,
                    NhDivisionRole,
                    NhDivisionUserRole,
                    NhDivisionRoleClaim,
                    NhLog,
                    NhLogMessageArgument,
                    NhLogFile,
                    NhLogMessageTranslated,
                    NhDbLogService,
                    SampleProjectManagementDbContext,
                    NhUserManager,
                    NhDivisionService,
                    NhDivisionMutateModel,
                    NhDivisionUserService,
                    NhDivisionUserMutateModel>(NewHeapAspNetCommonOptions.Builder(configuration).Build())
                .WithHangfire(
                    options => options.UseStorage(hostStorage),
                    consoleOptionsAction,
                    server =>
                    {
                        server.ServerName = hostName;
                        server.WorkerCount = 1;
                        server.SchedulePollingInterval = TimeSpan.FromMilliseconds(100);
                    });
            return services.BuildServiceProvider();
        }

        public async Task StartAsync()
        {
            foreach (var server in _hangfireServers)
            {
                await server.StartAsync(CancellationToken.None);
            }
        }

        public async Task StopAsync()
        {
            foreach (var server in _hangfireServers)
            {
                await server.StopAsync(CancellationToken.None);
            }
        }

        public async Task<SampleHangfireJobResult> RunConsoleJobAsync()
        {
            var probe = _provider.GetRequiredService<SampleHangfireJobProbe>();
            _provider.GetRequiredService<IBackgroundJobClient>()
                .Enqueue<SampleHangfireConsoleJob>("default", job => job.Run(null));
            return await probe.Completion.Task.WaitAsync(JobTimeout);
        }

        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();
            Storage.Dispose();
        }
    }
}

public sealed class SampleHangfireHostMarker(string name)
{
    public string Name { get; } = name;
}

public sealed record SampleHangfireJobResult(string HostName, JobStorage Storage, bool ConsoleAttached);

public sealed class SampleHangfireJobProbe
{
    public TaskCompletionSource<SampleHangfireJobResult> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public sealed class SampleHangfireConsoleJob(
    SampleHangfireHostMarker marker,
    SampleHangfireJobProbe probe,
    JobStorage storage)
{
    public void Run(PerformContext? context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.WriteLine($"Maintenance job running on {marker.Name}.");
        probe.Completion.TrySetResult(new SampleHangfireJobResult(
            marker.Name,
            storage,
            context.Items.ContainsKey("ConsoleContext")));
    }
}
