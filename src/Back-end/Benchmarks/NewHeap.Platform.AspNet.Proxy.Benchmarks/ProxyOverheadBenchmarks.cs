using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Reports;
using Perfolizer.Horology;

[MemoryDiagnoser]
[BenchmarkCategory("HttpRequest")]
[Config(typeof(Configuration))]
public class ProxyOverheadBenchmarks
{
    public sealed class Configuration : ManualConfig
    {
        public Configuration()
        {
            SummaryStyle = SummaryStyle.Default.WithTimeUnit(TimeUnit.Millisecond);
        }
    }

    private ProxyHosts _hosts = null!;
    private HttpClient _client = null!;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _hosts = new ProxyHosts();
        _client = LoadMeasurement.CreateClient();
        try
        {
            await _hosts.StartAsync();
            // Verify both paths and open connections before BenchmarkDotNet performs its own warmup.
            await Yarp_ForwardGetRequest();
            await NewHeap_ForwardGetRequest();
        }
        catch
        {
            await CleanupAsync();
            throw;
        }
    }

    [Benchmark(Baseline = true)]
    public Task Yarp_ForwardGetRequest() => LoadMeasurement.SendAsync(_client, _hosts.YarpUrl);

    [Benchmark]
    public Task NewHeap_ForwardGetRequest() => LoadMeasurement.SendAsync(_client, _hosts.NewHeapUrl);

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        _client?.Dispose();
        if (_hosts is not null)
        {
            await _hosts.DisposeAsync();
        }
    }
}
