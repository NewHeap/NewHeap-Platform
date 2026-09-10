using System.Diagnostics;

internal sealed class ProxyHosts : IAsyncDisposable
{
    internal const string AssemblyPathVariable = "NEWHEAP_PROXY_BENCHMARK_ASSEMBLY";
    private readonly List<Process> _processes = [];
    private readonly string _directory = Directory.CreateTempSubdirectory("newheap-proxy-benchmark-").FullName;
    internal string YarpUrl { get; private set; } = "";
    internal string NewHeapUrl { get; private set; } = "";

    internal async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var backend = await StartHostAsync("backend", "unused", cancellationToken);
        YarpUrl = await StartHostAsync("yarp", backend, cancellationToken) + "/benchmark";
        NewHeapUrl = await StartHostAsync("newheap", backend, cancellationToken) + "/benchmark";
    }

    private async Task<string> StartHostAsync(string mode, string upstream, CancellationToken cancellationToken)
    {
        // BenchmarkDotNet runs generated executables. Launch the original host with its own runtime/dependency files.
        var assembly = Environment.GetEnvironmentVariable(AssemblyPathVariable)
            ?? throw new InvalidOperationException("The benchmark host assembly was not configured.");
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true
        };
        foreach (var argument in new[] { assembly, "--host", mode, upstream, _directory })
        {
            start.ArgumentList.Add(argument);
        }

        var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {mode}.");
        _processes.Add(process);
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startup.CancelAfter(TimeSpan.FromSeconds(60));
        while (await process.StandardOutput.ReadLineAsync(startup.Token) is { } line)
        {
            if (line.StartsWith("READY ", StringComparison.Ordinal))
            {
                return line[6..];
            }
        }

        throw new InvalidOperationException($"{mode} exited before becoming ready. Inspect its error output.");
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var process in _processes.AsEnumerable().Reverse())
        {
            try
            {
                if (!process.HasExited)
                {
                    await process.StandardInput.WriteLineAsync("stop");
                    using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await process.WaitForExitAsync(shutdown.Token);
                }
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
            finally
            {
                process.Dispose();
            }
        }

        _processes.Clear();
        // This absolute directory is created exclusively by this instance; it never contains user proxy data.
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
