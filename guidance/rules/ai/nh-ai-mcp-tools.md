---
id: nh-ai-mcp-tools
title: "Expose generated AI tools through MCP"
area: backend
reference: ai-mcp-tools
summary: "Adapt an explicitly MCP-exposed generated NewHeap catalog through the official SDK without duplicating domain code or bypassing discovery and invocation authorization."
sample-cases: ["SPM-222", "SPM-240"]
public-symbols: ["INhAiMcpToolAdapter", "WithNewHeapPlatformAITools", "CallNewHeapToolAsync", "CallNewHeapFlatToolAsync", "NhAiMcpToolException", "NhAiToolExportSchema"]
skills: ["newheap-backend-development"]
providers: ["provider-neutral"]
risk: high
---
## Preferred approach

Add `NewHeap.Platform.AI.Mcp` and `NewHeap.Platform.AI.AspNet.Common`, then call
`WithNewHeapPlatformAITools` on the official `IMcpServerBuilder`. Mark only
reviewed tools with `NhAiToolExposure.Mcp`; keep local and MCP exposure explicit
flags on the same generated contract. The integration resolves an authenticated
invocation context for every list and call request, then creates MCP tools per actor
and tenant through `INhAiMcpToolAdapter`. The adapter asks the shared default-deny
discovery service for visible descriptors and wraps the same generated
`AIFunction` delegates used for local execution.

Configure the official MCP transport, authentication, and endpoint in the host.
For HTTP, prefer the official stateless Streamable HTTP transport unless a
reviewed feature genuinely requires session state. Keep official authorization
filters enabled where applicable, but retain the NewHeap discovery policy and
invocation gate: transport authorization does not replace application resource
authorization.

Expected `TaskResult` failures remain structured tool results and are marked as
MCP tool errors rather than JSON-RPC protocol failures. Cancellation propagates.
Unexpected exceptions are handled once at the protocol boundary without
returning internal exception detail.

For a typed .NET client that intentionally calls a generated NewHeap tool, use
`CallNewHeapToolAsync<TInput, TOutput>`. Pass the generated tool input directly;
the helper creates the required `input` envelope and returns the deserialized
`TaskResult<T>.data` value. A failed tool result becomes `NhAiMcpToolException`,
which preserves the original `CallToolResult` for structured handling. Use the
official `CallToolAsync` API directly for independently registered MCP tools,
including consumer-owned write tools that do not use the generated NewHeap
contract. Supply explicit serializer options when a source-generated JSON
context or null-omission policy owns the wire schema.

When an external wire contract already owns the argument and result shape of a
generated tool, declare `ExportSchema = NhAiToolExportSchema.Flat` on that tool
next to its `NhAiToolExportName`. The MCP export then publishes the input type's
properties as the top-level arguments and the output type as the structured
result; a failed `TaskResult<T>` that carries typed data publishes that data as
the structured error payload, and a failure without data becomes a plain MCP tool
error. The input type must be an object; the generator rejects other input types
with `NHAI012`. Local and agent execution keep the generated envelope, and the
governed `AIFunction` still runs through the shared invoker. Call a flat tool
through `CallNewHeapFlatToolAsync<TInput, TOutput>` or the official
`CallToolAsync` with the input properties as arguments.

Other libraries may register independently governed MCP tools through
`WithTools`, `WithToolsFromAssembly`, or manual `McpServerTool` services. Keep
their export names distinct from every NewHeap-managed export. Startup rejects
name collisions and SDK registrations whose metadata identifies them as a
NewHeap tool, because those form a second publication path. Only source-generated
`INhAiGeneratedToolCatalog` implementations enter the NewHeap export path.

## Avoid

- Adding separate MCP methods that copy generated domain tool implementations.
- Maintaining application-specific lists of generated tool names solely to wrap
  input and unwrap `TaskResult<T>` responses.
- Registering a consumer-owned write through the MCP SDK because the generated
  export envelope does not match its wire contract; declare a flat export schema
  on the generated tool instead.
- Calling independently registered MCP tools through the NewHeap client helper.
- Publishing a NewHeap tool both through its generated catalog and through SDK tool registration.
- Giving an external MCP tool the same wire name as a NewHeap-managed export.
- Publishing every local tool remotely or treating catalog membership as authorization.
- Building one global actor-independent tool list for a multi-tenant host.
- Trusting MCP arguments, prompts, retrieved content, or headers as scope authorization.
- Returning credentials, internal exception text, prompts, or raw audit content over MCP.
- Using MCP Tasks as NewHeap's authoritative durable-operation store.

## Verification

Use the official in-memory stream transport to list and invoke the generated
tool without a network or live model. Verify an unauthorized context receives no
NewHeap tool, a direct call still passes the invocation gate, cancellation
propagates, failed `TaskResult` values remain structured tool errors, an external
SDK tool with a distinct name remains available, and an export-name collision
fails at startup. Also call the generated tool through
`CallNewHeapToolAsync<TInput, TOutput>`, omit an optional null property through
explicit serializer options, assert the typed data is returned, and assert a
failed result becomes `NhAiMcpToolException` without changing direct SDK calls.
For a flat export, assert the listed input schema exposes the input properties
without an `input` wrapper, that the structured result is the output type without
a `success` envelope, and that a typed denial remains available as the structured
content of the failed `CallToolResult`. SPM-222 and SPM-240 are the executable
references.
