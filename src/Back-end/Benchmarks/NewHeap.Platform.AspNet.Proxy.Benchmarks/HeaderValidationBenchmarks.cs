using BenchmarkDotNet.Attributes;
using NewHeap.Platform.AspNet.Proxy;

[MemoryDiagnoser]
public class HeaderValidationBenchmarks
{
    [GlobalSetup]
    public void CheckImplementations()
    {
        (string? Name, bool Expected)[] cases =
        [
            ("Host", false), ("host", false), ("X-Request-Id", true),
            (null, false), ("", false), ("Bad Header", false), ("Bad\r\nHeader", false),
            ("X-Forwarded-For", false), ("Access-Control-Allow-Origin", false),
            ("Forwarded", false), ("Authorization", false), ("X-Custom-!", true), ("X-é", false)
        ];
        foreach (var (name, expected) in cases)
        {
            if (IsSafeHeaderWithoutSpan(name) != expected
                || NhProxyConfigurationValidator.IsSafeHeader(name) != expected)
            {
                throw new InvalidOperationException($"Header benchmark implementations disagree with the expected result for '{name}'.");
            }
        }
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("RejectHost")]
    public bool IsSafeHeader_RejectHost_Linq() => IsSafeHeaderWithoutSpan("Host");

    [Benchmark]
    [BenchmarkCategory("RejectHost")]
    public bool IsSafeHeader_RejectHost_Span() => NhProxyConfigurationValidator.IsSafeHeader("Host");

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("AcceptRequestId")]
    public bool IsSafeHeader_AcceptRequestId_Linq() => IsSafeHeaderWithoutSpan("X-Request-Id");

    [Benchmark]
    [BenchmarkCategory("AcceptRequestId")]
    public bool IsSafeHeader_AcceptRequestId_Span() => NhProxyConfigurationValidator.IsSafeHeader("X-Request-Id");

    // Preserve the implementation before the IsToken span optimization as the benchmark baseline.
    private static bool IsTokenWithoutSpan(string? value) => !string.IsNullOrEmpty(value)
        && value.All(c => char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c));

    private static bool IsSafeHeaderWithoutSpan(string? name) => IsTokenWithoutSpan(name)
        && !new[] { "Authorization", "Proxy-Authorization", "Cookie", "Set-Cookie", "Host", "Content-Length", "Transfer-Encoding",
            "Connection", "Upgrade", "Keep-Alive", "TE", "Trailer", "Proxy-Authenticate", "WWW-Authenticate",
            "Content-Security-Policy", "Strict-Transport-Security", "X-Frame-Options", "X-Content-Type-Options",
            "X-Api-Key", "Api-Key", "X-Auth-Token" }
            .Contains(name, StringComparer.OrdinalIgnoreCase)
        && !name!.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase)
        && !name.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase)
        && !name.Equals("Forwarded", StringComparison.OrdinalIgnoreCase);
}
