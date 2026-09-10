using System.Globalization;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;

internal static class LoadBenchmark
{
    internal static async Task RunAsync(string[] args)
    {
        if (args is ["--help"])
        {
            Console.WriteLine("Proxy load test: --seconds 10 --warmup 5 --rounds 4 --concurrency 32 --output <file.json>");
            return;
        }

#if DEBUG
        throw new InvalidOperationException("Build and run the benchmark with --configuration Release.");
#endif
        var seconds = 10;
        var warmup = 5;
        var rounds = 4;
        var concurrency = 32;
        var output = Path.GetFullPath($"tmp/proxy-load-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.json");
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 == args.Length)
            {
                throw new ArgumentException($"Missing value for {args[index]}.");
            }

            var value = args[index + 1];
            if (args[index] == "--output")
            {
                output = Path.GetFullPath(value);
                continue;
            }

            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 1)
            {
                throw new ArgumentException($"{args[index]} requires a positive integer.");
            }

            switch (args[index])
            {
                case "--seconds": seconds = number; break;
                case "--warmup": warmup = number; break;
                case "--rounds": rounds = number; break;
                case "--concurrency" when number <= 1024: concurrency = number; break;
                default: throw new ArgumentException($"Unknown option or out-of-range value: {args[index]}.");
            }
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancel;
        try
        {
            await using var hosts = new ProxyHosts();
            await hosts.StartAsync(cancellation.Token);
            var urls = new Dictionary<string, string>
            {
                ["yarp"] = hosts.YarpUrl,
                ["newheap"] = hosts.NewHeapUrl
            };
            using var client = LoadMeasurement.CreateClient();
            var rows = new List<Run>();
            Console.WriteLine($"HTTP/1.1, 1024 bytes, concurrency {concurrency}, {rounds} rounds, {seconds}s per measurement.");
            Console.WriteLine("Variant   Round   Requests/sec   Mean response (ms)   Errors");
            for (var round = 1; round <= rounds; round++)
            {
                string[] order = round % 2 == 1 ? ["yarp", "newheap"] : ["newheap", "yarp"];
                foreach (var variant in order)
                {
                    Console.WriteLine($"Warming {variant} for {warmup}s (round {round})...");
                    var warmed = await LoadMeasurement.MeasureAsync(client, urls[variant], concurrency,
                        TimeSpan.FromSeconds(warmup), cancellation.Token);
                    if (warmed.Errors != 0 || warmed.Requests == 0)
                    {
                        throw new InvalidOperationException($"{variant} warmup failed: {warmed.Requests} valid responses, {warmed.Errors} errors.");
                    }

                    var measured = await LoadMeasurement.MeasureAsync(client, urls[variant], concurrency,
                        TimeSpan.FromSeconds(seconds), cancellation.Token);
                    rows.Add(new(variant, round, measured));
                    Print(variant, round.ToString(CultureInfo.InvariantCulture), measured);
                }
            }

            var baseline = Measurement.Combine(rows.Where(row => row.Variant == "yarp").Select(row => row.Measurement));
            var newheap = Measurement.Combine(rows.Where(row => row.Variant == "newheap").Select(row => row.Measurement));
            var valid = rows.All(row => row.Measurement.Errors == 0 && row.Measurement.Requests > 0);
            var comparison = valid ? new
            {
                ThroughputChangePercent = (newheap.RequestsPerSecond / baseline.RequestsPerSecond - 1) * 100
            } : null;
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new
            {
                CreatedUtc = DateTimeOffset.UtcNow, Runtime = RuntimeInformation.FrameworkDescription,
                OperatingSystem = RuntimeInformation.OSDescription, Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                Environment.ProcessorCount, GCSettings.IsServerGC,
                YarpVersion = typeof(Yarp.ReverseProxy.Configuration.RouteConfig).Assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                Configuration = new { seconds, warmup, rounds, concurrency, PayloadBytes = LoadMeasurement.Payload.Length },
                Valid = valid, Runs = rows, Baseline = baseline, NewHeap = newheap, Comparison = comparison
            }, new JsonSerializerOptions { WriteIndented = true }), cancellation.Token);
            Console.WriteLine($"Results: {output}");
            if (!valid)
            {
                throw new InvalidOperationException("Invalid comparison: requests failed or no valid responses were received. Inspect the saved results.");
            }

            Print("yarp", "all", baseline);
            Print("newheap", "all", newheap);
            Console.WriteLine(FormattableString.Invariant($"Throughput change vs YARP: {comparison!.ThroughputChangePercent:+0.00;-0.00;0.00}%"));
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
        }
    }

    private static void Print(string variant, string round, Measurement measurement)
    {
        Console.WriteLine(FormattableString.Invariant($"{variant,-9} {round,5} {measurement.RequestsPerSecond,14:F1} {measurement.MeanResponseMilliseconds,20:F3} {measurement.Errors,8}"));
    }

    private sealed record Run(string Variant, int Round, Measurement Measurement);
}
