using System.Diagnostics;
using System.Net;

internal sealed record Measurement(long Requests, long Errors, double ElapsedSeconds, double TotalResponseMilliseconds)
{
    public double RequestsPerSecond => Requests / ElapsedSeconds;
    public double MeanResponseMilliseconds => Requests == 0 ? 0 : TotalResponseMilliseconds / Requests;

    internal static Measurement Combine(IEnumerable<Measurement> values)
    {
        var items = values.ToArray();
        return new(items.Sum(value => value.Requests), items.Sum(value => value.Errors),
            items.Sum(value => value.ElapsedSeconds), items.Sum(value => value.TotalResponseMilliseconds));
    }
}

internal static class LoadMeasurement
{
    internal static readonly byte[] Payload = Enumerable.Repeat((byte)'x', 1024).ToArray();

    internal static HttpClient CreateClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
        AutomaticDecompression = DecompressionMethods.None
    })
    {
        DefaultRequestVersion = HttpVersion.Version11,
        DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        Timeout = TimeSpan.FromSeconds(10)
    };

    internal static async Task SendAsync(HttpClient client, string url, CancellationToken cancellationToken = default)
    {
        using var response = await client.GetAsync(url, cancellationToken);
        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK || !body.AsSpan().SequenceEqual(Payload))
        {
            throw new HttpRequestException($"Expected HTTP 200 and the benchmark payload; received {(int)response.StatusCode}.");
        }
    }

    internal static async Task<Measurement> MeasureAsync(HttpClient client, string url, int concurrency, TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        var start = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var workers = Enumerable.Range(0, concurrency).Select(async _ =>
        {
            var started = await start.Task;
            long requests = 0;
            long errors = 0;
            double responseMilliseconds = 0;
            while (Stopwatch.GetElapsedTime(started) < duration)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var requestStarted = Stopwatch.GetTimestamp();
                try
                {
                    await SendAsync(client, url, cancellationToken);
                    var elapsed = Stopwatch.GetElapsedTime(requestStarted).TotalMilliseconds;
                    requests++;
                    responseMilliseconds += elapsed;
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested
                    && exception is HttpRequestException or OperationCanceledException)
                {
                    errors++;
                }
            }

            return new Measurement(requests, errors, 0, responseMilliseconds);
        }).ToArray();

        var started = Stopwatch.GetTimestamp();
        start.SetResult(started);
        var results = await Task.WhenAll(workers);
        // Drain in-flight requests and include their time; never discard slow requests at the deadline.
        return Measurement.Combine(results) with { ElapsedSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds };
    }

    internal static async Task CheckAsync()
    {
        var combined = Measurement.Combine([new(10, 1, 2, 20), new(30, 2, 3, 120)]);
        if (combined.RequestsPerSecond != 8 || combined.MeanResponseMilliseconds != 3.5 || combined.Errors != 3)
        {
            throw new InvalidOperationException("Aggregation must weight by elapsed time and successful request count.");
        }

        using var client = new HttpClient(new CheckHandler());
        var result = await MeasureAsync(client, "http://benchmark.invalid", 1, TimeSpan.FromMilliseconds(100));
        if (result.Requests != 1 || result.Errors < 4 || result.ElapsedSeconds < 0.1 || result.MeanResponseMilliseconds <= 0)
        {
            throw new InvalidOperationException("Full-body validation, HTTP errors, transport errors or timing failed.");
        }

        Console.WriteLine("Benchmark self-test passed.");
    }

    private sealed class CheckHandler : HttpMessageHandler
    {
        private int _calls;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(1, cancellationToken);
            return ++_calls switch
            {
                1 => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) },
                2 => new(HttpStatusCode.InternalServerError) { Content = new ByteArrayContent(Payload) },
                3 => throw new HttpRequestException("Expected self-test failure."),
                4 => throw new TaskCanceledException("Expected self-test timeout."),
                _ => new(HttpStatusCode.OK) { Content = new ByteArrayContent([0]) }
            };
        }
    }
}
