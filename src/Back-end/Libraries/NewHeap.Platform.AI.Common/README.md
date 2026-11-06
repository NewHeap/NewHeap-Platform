# NewHeap.Platform.AI.Common

Provider-neutral contracts for exposing application-owned AI tools through
`Microsoft.Extensions.AI`. Tool execution is fail-closed: consumers provide an
`INhAiToolInvocationGate` that resolves and authorizes the invocation context
before application code runs. A host that only uses model profiles needs no
gate; it passes container validation, and resolving `INhAiToolInvoker` there
fails with a clear configuration error. Tool catalogs may be registered as
singletons or, when their tools depend on the current run, as scoped services;
startup validation resolves them in a scope of its own.

Every governed tool gives the model or protocol caller one result shape,
`NhAiToolResult`: `{ "success": true, "data": ... }` on success and
`{ "success": false, "code": "...", "message": "..." }` on failure, plus `data`
when a failed `TaskResult<T>` carries a typed payload. The code is the first result
item named with a lowercase dash-case code, such as the pipeline's
`ai-tool-budget-denied` or an application's own `project-not-found`; a failure
without one is `ai-tool-failed` and keeps every message with its field name.
Generated and API bridge tools write it through `NhAiToolResult.CreateMarshaller<T>`,
and `NhAiGovernedAIFunction` publishes the shape around the descriptor's output
schema as its return schema. The shape is the same for every tool and is not part
of a contract hash. A hand-written attested catalog creates its functions the same
way, and uses `NhAiToolResult.Failed` for a call it refuses before the invoker runs.

A tool's `MaxConcurrency` bounds its concurrent invocations per process. The
default limiter keeps a separate bound per tool and tenant, so one tenant's load
never holds up another tenant's call; invocations without a tenant share one
bound. A further call waits in line for a free slot until the tool's timeout or
the invocation's deadline on the application's clock, and is refused with
`ai-tool-concurrency-limited` only when that wait ends first. Use
`UseInProcessConcurrencyLimiter` with `NhAiToolConcurrencyPartition.Tool` to
share one bound across tenants, or `UseConcurrencyLimiter<TLimiter>()` for a
limiter shared between instances; it waits only when it implements
`WaitToAcquireAsync`.

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
standard Microsoft chat response types. A call that fails at the provider
returns `ai-model-dependency-unavailable` with its content-free status, provider
code and kind (`NhAiModelProviderFailure`) and logs them as a warning; register
an `INhAiModelProviderFailureReader` for a provider SDK whose exception type the
executor does not know. Hierarchical context resolution
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
