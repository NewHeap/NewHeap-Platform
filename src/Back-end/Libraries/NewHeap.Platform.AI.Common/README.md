# NewHeap.Platform.AI.Common

Provider-neutral contracts for exposing application-owned AI tools through
`Microsoft.Extensions.AI`. Tool execution is fail-closed: consumers provide an
`INhAiToolInvocationGate` that resolves and authorizes the invocation context
before application code runs.

Provider-backed `IChatClient` instances remain consumer-owned. Register them as
keyed services, then map stable application intent to those keys with
`AddNewHeapPlatformAI` and `AddChatProfile`. Profiles declare capabilities,
classification and region policy, request budgets, timeouts, streaming,
fallbacks, routing tags, and evaluation baselines. The resolver returns the
selected Microsoft client plus a bounded, content-free decision trace.

Invocation context contributors add ordered, bounded execution scopes and
capability grants without introducing an ASP.NET dependency. Safe outcome,
audit, usage, budget, and retention contracts contain operational metadata but
no prompt, response, tool-argument, credential, or retrieved-document fields.

`INhAiChatExecutor` applies profile and run budgets, deadlines, streaming
cleanup, content-free OpenTelemetry and usage accounting while returning the
standard Microsoft chat response types. Hierarchical context resolution
authorizes before retrieval and preserves provenance, trust, conflicts,
replacement and budget traces.

The ingestion pipeline uses the standard Microsoft VectorData abstractions. It
authorizes before reading a source, creates deterministic versioned records,
preserves classification and scope filters, reports partial batch outcomes, and
supports explicit replacement lineage and authorized deletion. Concrete vector
providers and their configuration remain application-owned.

The package does not contain model-provider clients, credentials, prompts, or
remote tool transports. Use `NewHeap.Platform.AI.Generators` to generate the
local `AIFunction` catalog from attributed application methods.
