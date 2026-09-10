using System.Net;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AspNet.Proxy;
using NewHeap.Platform.AspNet.Proxy.Sqlite;
using Yarp.ReverseProxy.Configuration;

if (args is ["--self-test"])
{
    await LoadMeasurement.CheckAsync();
    return;
}

if (args is not ["--host", var mode, var upstream, var directory])
{
    Environment.SetEnvironmentVariable(ProxyHosts.AssemblyPathVariable, typeof(ProxyOverheadBenchmarks).Assembly.Location);
    if (args is ["--load", .. var loadArgs])
    {
        await LoadBenchmark.RunAsync(loadArgs);
        return;
    }

    if (!args.Any(argument => argument is "--filter" or "-f"
        || argument.StartsWith("--filter=", StringComparison.Ordinal)
        || argument.StartsWith("-f=", StringComparison.Ordinal)))
    {
        args = ["--filter", "*", ..args];
    }

    var summaries = BenchmarkSwitcher.FromTypes([typeof(ProxyOverheadBenchmarks), typeof(HeaderValidationBenchmarks)])
        .Run(args, DefaultConfig.Instance
            .AddLogicalGroupRules(BenchmarkLogicalGroupRule.ByCategory));
    if (summaries.Any(summary => summary.HasCriticalValidationErrors
        || summary.Reports.Any(report => !report.Success)))
    {
        Environment.ExitCode = 1;
    }

    return;
}

if (mode is not ("backend" or "yarp" or "newheap"))
{
    throw new ArgumentException("Unknown benchmark host.");
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = [], ContentRootPath = AppContext.BaseDirectory, EnvironmentName = Environments.Production
});
builder.Configuration.Sources.Clear();
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(server =>
    server.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http1));

if (mode == "yarp")
{
    builder.Services.AddReverseProxy().LoadFromMemory(
        [new RouteConfig
        {
            RouteId = "benchmark", ClusterId = "backend", Match = new RouteMatch { Path = "/benchmark" }
        }],
        [new ClusterConfig
        {
            ClusterId = "backend",
            Destinations = new Dictionary<string, DestinationConfig>
            {
                ["backend"] = new() { Address = upstream }
            }
        }]);
}
else if (mode == "newheap")
{
    var storage = new NhProxySqliteOptions { DatabasePath = Path.Combine(directory, "proxy.db") };
    // Seed before startup; measured requests use the normal persisted configuration and middleware.
    await using (var store = new NhProxySqliteConfigurationStore(Options.Create(storage)))
    {
        await store.InitializeAsync();
        var clusterId = Guid.NewGuid();
        var saved = await store.SaveRewritesAsync(new NhProxyRewriteSaveRequest(0,
            [new NhProxyRewriteRule
            {
                Id = Guid.NewGuid(), Name = "Benchmark", ClusterId = clusterId,
                Match = new NhProxyRewriteMatch { Path = "/benchmark" }
            }],
            [new NhProxyCluster
            {
                Id = clusterId, Name = "Backend", Destination = new NhProxyDestination("backend", new Uri(upstream))
            }]));
        if (!saved.Success)
        {
            throw new InvalidOperationException("Could not seed benchmark configuration.");
        }
    }

    builder.Services.AddNewHeapProxy(configureStorage: options => options.DatabasePath = storage.DatabasePath);
}

await using var app = builder.Build();
if (mode == "backend")
{
    app.MapGet("/benchmark", () => Results.Bytes(LoadMeasurement.Payload, "application/octet-stream"));
}
else if (mode == "yarp")
{
    app.MapReverseProxy();
}
else
{
    app.UseNewHeapProxy();
}

await app.StartAsync();
if (mode == "newheap")
{
    var status = app.Services.GetRequiredService<INhProxyConfigurationService>().GetStatus();
    if (status.Rewrite.State != NhProxyActivationState.Active || status.Redirect.State != NhProxyActivationState.Active)
    {
        throw new InvalidOperationException("Benchmark configuration is not active.");
    }
}

Console.WriteLine($"READY {app.Urls.Single()}");
await Console.In.ReadLineAsync();
await app.StopAsync();
