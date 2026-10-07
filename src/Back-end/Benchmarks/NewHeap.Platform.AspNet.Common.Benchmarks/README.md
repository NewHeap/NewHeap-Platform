# Selected collection encoding benchmark

Run from this directory so BenchmarkDotNet can locate the project directly:

```powershell
dotnet run -c Release -- --filter '*' --iterationCount 10 --warmupCount 5
```

The benchmark compares UTF-8 Newtonsoft JSON and the compatibility-first
`NhSelectedCollectionMessagePack` writer. Each case encodes 50 synthetic rows,
with strings, decimals, booleans and dates plus the same collection/selection
metadata. It measures encoding and allocations only, not database work, HTTP
compression, network latency, browser decoding or rendering.

Measured 2026-10-07 on Windows 11, Intel Core Ultra 9 275HX, .NET 10.0.12,
SDK 10.0.401, BenchmarkDotNet 0.15.8 ShortRun with overrides (one launch, five
warmup and ten measurement iterations). No concurrent builds were run during
this final measurement:

| Fields | Encoding | Mean | Allocated | Uncompressed bytes |
|---|---|---:|---:|---:|
| 6 | JSON | 20.43 us | 51.13 KB | 7,825 |
| 6 | MessagePack | 55.15 us | 338.26 KB | 6,736 |
| 18 | JSON | 57.49 us | 146.98 KB | 23,827 |
| 18 | MessagePack | 171.42 us | 1,222.84 KB | 20,544 |

MessagePack is about 14% smaller here, but 2.70–2.98 times slower and allocates
6.62–8.32 times more memory. This implementation honors the application's JSON
contract by constructing tokens before writing MessagePack; it is a compatibility
baseline, not a demonstrated speed optimization. Keep JSON as the default.
The 99.9% confidence-interval half-widths are 0.517/1.934 us for six-field
JSON/MessagePack and 1.656/3.980 us for eighteen fields. Earlier three-iteration
exploratory runs had much wider intervals, so they are superseded by this run.
These numbers are still not an end-to-end performance claim.

Field reduction itself decreases work and payload in both encodings. Before
changing defaults, benchmark representative projected database queries, compressed
responses, browser decoding and rendering under realistic concurrency. A direct
writer could avoid token allocation, but must first prove parity for names, dates,
enums, nullable values and custom application serializer settings.

Real SQL Server/PostgreSQL execution tests are in `FieldSelectionTests`. In this
workstation run, Docker Desktop failed during inference-manager startup, so those
two execution tests remained unverified. Both provider translation tests passed.
