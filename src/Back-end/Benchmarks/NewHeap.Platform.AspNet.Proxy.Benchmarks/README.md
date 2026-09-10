# Proxy benchmarks

Use **BenchmarkDotNet** to measure request-time overhead against a standard YARP
application. A separate, opt-in HTTP load test measures requests/sec. Its results
are not included in the BenchmarkDotNet report.

## Shared workload

The baseline uses `AddReverseProxy().LoadFromMemory(...)` and `MapReverseProxy()`.
NewHeap uses its normal `AddNewHeapProxy(...)` and `UseNewHeapProxy()` entry points.
Both forward the same GET through one exact `/benchmark` route to a backend that
returns a fixed 1 KiB response. Both use the centrally pinned YARP version.

NewHeap starts with a fresh SQLite database containing one managed rewrite and
no redirects. Its administration branch, authentication middleware, redirect checks
and chain checks remain installed. Configuration is seeded and activated before
measurement. No library behavior or defaults are changed. Startup, administration,
database writes, regex rules and large rule sets are outside this workload.

The backend and proxies run in separate processes on dynamically assigned IPv4
loopback ports, with logging disabled and no TLS or application telemetry. Both
tests use the same HTTP client configuration and validate HTTP 200 plus the entire
response body. Connections are reused with HTTP/1.1; redirects, cookies, system
proxies and decompression are disabled. Requests time out after 10 seconds.

## Request-time overhead with BenchmarkDotNet

Prerequisite: the SDK selected by `src/Back-end/global.json`. The pinned
BenchmarkDotNet package is restored automatically. From the repository root:

```sh
npm run benchmark:proxy
npm run benchmark:proxy -- --job short
```

These commands run all six benchmark methods and produce a separate table and
report for each benchmark class, with a separate row for each case:

| Method | Workload |
| --- | --- |
| `Yarp_ForwardGetRequest` | Complete GET through standard YARP (HTTP baseline). |
| `NewHeap_ForwardGetRequest` | The same GET through NewHeap. |
| `IsSafeHeader_RejectHost_Linq` | Reject `Host` with the original LINQ token validation (baseline). |
| `IsSafeHeader_RejectHost_Span` | Reject `Host` with the current span token validation. |
| `IsSafeHeader_AcceptRequestId_Linq` | Accept `X-Request-Id` with the original LINQ token validation (baseline). |
| `IsSafeHeader_AcceptRequestId_Span` | Accept `X-Request-Id` with the current span token validation. |

Each case has its own `[Benchmark]` method. HTTP requests, rejecting `Host` and
accepting `X-Request-Id` have separate logical groups, each with its own baseline.
Each span variant is compared with the original implementation for the same input;
only the HTTP requests are compared against YARP.
To select a subset, filter by class or method name:

```sh
npm run benchmark:proxy -- --filter '*ProxyOverheadBenchmarks*'
npm run benchmark:proxy -- --filter '*HeaderValidationBenchmarks*'
npm run benchmark:proxy -- --filter '*IsSafeHeader_RejectHost*'
```

Or directly from `src/Back-end`:

```sh
dotnet run --project Benchmarks/NewHeap.Platform.AspNet.Proxy.Benchmarks -c Release --no-launch-profile
```

`Yarp_ForwardGetRequest` has `[Benchmark(Baseline = true)]`;
`NewHeap_ForwardGetRequest` is the comparison. Each invocation sends and awaits **one complete request**, with no
concurrent load or manual timing loop. Global setup starts all hosts, activates
configuration and verifies both paths; global cleanup stops them and removes only
their own temporary storage. Setup and cleanup are outside measured invocations.

BenchmarkDotNet owns warmup, invocation counts, iterations, timing and statistical
reporting. Its normal process isolation remains enabled. The summary includes
`Mean`, `Error`, `StdDev` and the baseline `Ratio`. The HTTP table uses milliseconds.
The header table uses BenchmarkDotNet's automatic time unit, normally nanoseconds.
A NewHeap ratio of 1.10 indicates approximately 10% more time per operation;
the difference between the two means expresses that overhead in milliseconds.
Consider error, spread and BenchmarkDotNet warnings before interpreting a small
difference. `--job short` is a quicker exploratory run with fewer samples.

Reports are written to `src/Back-end/BenchmarkDotNet.Artifacts/results/` by the
commands above. BenchmarkDotNet also supports its normal CLI arguments, including
`--filter`, `--exporters` and `--artifacts`; use `--help` for details.

This is an **end-to-end request-time comparison**. It includes the HTTP client,
loopback networking, backend and response validation, so the delta is not an
isolated measurement of middleware CPU time. All processes share machine
resources. Run Release without a debugger, close competing workloads and repeat
measurements. We do not derive maximum requests/sec from the BenchmarkDotNet
mean.

Both benchmark classes enable `MemoryDiagnoser`. Its `Allocated` column reports
managed allocations per operation, and its GC columns report collections per
1,000 operations; it does not measure retained heap size or process working set.
For `ProxyOverheadBenchmarks`, these are **benchmark-client allocations**, because
the backend and proxies run in separate processes. They are not proxy-memory
measurements. The span methods in `HeaderValidationBenchmarks` invoke the actual
internal header filter in the measured process, so their allocation figures do
cover that library operation. The LINQ methods preserve the original implementation
inside the benchmark project for comparison. Only token-character validation
changed from LINQ `All` to span `ContainsAnyExcept`; the denylist and prefix checks
are identical. Setup verifies both implementations against expected outcomes,
outside timed invocations. Use process-level profiling for the complete proxy's
memory consumption.

See BenchmarkDotNet's [baseline documentation](https://benchmarkdotnet.org/articles/features/baselines.html)
and [setup/cleanup lifecycle](https://benchmarkdotnet.org/articles/features/setup-and-cleanup.html).

## Separate requests/sec load test

From the repository root:

```sh
npm run benchmark:proxy:load
npm run benchmark:proxy:load -- --seconds 15 --warmup 5 --rounds 4 --concurrency 32
```

Directly from `src/Back-end`, pass `--load` before the load-test options:

```sh
dotnet run --project Benchmarks/NewHeap.Platform.AspNet.Proxy.Benchmarks -c Release --no-launch-profile -- --load --seconds 15 --warmup 5 --rounds 4 --concurrency 32
```

Defaults are 10 measured seconds, 5 warmup seconds before each measurement,
4 rounds and 32 concurrent requests. Concurrency accepts 1 through 1024; other
numeric options require positive integers. The runner alternates proxy order;
use an even number of rounds to balance it. Warmup is excluded from results.

Throughput is successful, body-validated requests / actual elapsed seconds.
The report also shows mean response time under that concurrency, including
queueing. At the deadline, workers stop issuing requests and drain in-flight
responses; their time and completions remain in the measurement. Aggregates use
total requests / total elapsed seconds, and total response time / total requests.
Failed requests invalidate the comparison and cause a nonzero exit. A failed
warmup aborts immediately.

Raw rounds, aggregate values, throughput change and runtime/OS/YARP metadata are
saved to `src/Back-end/tmp/proxy-load-*.json`. Use `--output <file.json>` to select
a path, relative to `src/Back-end` with the commands above. Existing output files
are replaced. Keep the tested Git revision alongside shared results.

This custom load test is exploratory. Each worker waits for its response before
issuing another request (closed-loop load). Client or backend saturation can hide
proxy overhead. Sweep concurrency rather than treating a single setting as
maximum capacity. Its mean response time is not the BenchmarkDotNet overhead
result or an open-loop production latency distribution.

## Verify

From the repository root:

```sh
npm run benchmark:proxy -- --self-test
npm run benchmark:proxy -- --job dry
npm run benchmark:proxy:load -- --seconds 1 --warmup 1 --rounds 2 --concurrency 4
```

The self-test checks weighted load aggregation and shared response validation,
including invalid bodies, HTTP errors, transport failures and timeouts. The
BenchmarkDotNet Dry job exercises all six benchmark methods, generated executables,
setup and cleanup. It is a smoke check, not evidence of performance; its first
invocation includes JIT effects. The short load run checks actual forwarding and
JSON output. No absolute performance threshold is enforced in CI.

The existing sample cases SPM-238 and SPM-239 document the proxy behavior exercised
here. This standalone harness adds no public library or consumer usage contract.
SQLite is exercised; SQL Server and PostgreSQL remain outside the existing proxy
storage implementation and are not benchmark variants.
