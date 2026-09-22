---
id: nh-ai-mcp-tools
title: "Expose generated AI tools through MCP"
area: backend
reference: ai-mcp-tools
summary: "Adapt an explicitly MCP-exposed generated NewHeap catalog through the official SDK without duplicating domain code or bypassing discovery and invocation authorization."
sample-cases: ["SPM-222", "SPM-240"]
public-symbols: ["INhAiMcpToolAdapter", "WithNewHeapPlatformAITools", "CallNewHeapToolAsync", "CallNewHeapFlatToolAsync", "NhAiMcpToolException", "NhAiMcpResultMetadata", "NhAiToolAnnotationHints", "NhAiToolExportSchema", "NhAiToolHint", "NhAiToolJsonSerializerOptions"]
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
the structured error payload. The input type must be an object; the generator
rejects other input types with `NHAI012`. Local and agent execution keep the
generated envelope, and the governed `AIFunction` still runs through the shared
invoker. Call a flat tool through `CallNewHeapFlatToolAsync<TInput, TOutput>` with
the tool set's serializer options, or through the official `CallToolAsync` with
the input properties as arguments.

A flat export writes its structured result, text block and typed error payload
with the tool set's declared `JsonSerializerContext`. Without a declared context it
uses `NhAiToolJsonSerializerOptions.FlatExport`: camelCase names, string enums,
relaxed escaping, compact output and every property written, including nulls.
Declare a context when another wire contract applies; a byte-identical external
contract should always declare one. Enveloped exports without a context keep the
Microsoft.Extensions.AI defaults, which omit nulls.

The generated input and output schemas describe the effective serializer
contract. They follow the declared context's naming policy, default ignore
condition and enum converter, or the defaults above. `JsonIgnore` without a
condition removes a property; `Never` keeps it required; `WhenWritingNull` and
`WhenWritingDefault` keep it optional; `WhenWriting` and `WhenReading` remove it
only from the output or input schema respectively. Correcting a schema changes the
schema and contract hash, so treat it as a contract change for pinned manifests
and bound proposals.

Every failed governed invocation publishes its stable failure detail. The
`_meta` object carries `NhAiMcpResultMetadata.CodeKey`, `MessageKey`,
`EvidenceReferenceKey` when present, and `TypedPayloadKey`. A failure without
typed data publishes structured content `{ code, message, evidenceReference }` and
the text `code: message`, for flat and enveloped exports alike. The invocation
pipeline owns stable `NhAiToolFailureCodes` such as `ai-tool-budget-denied`; a
consumer failure keeps its own result code. Both client helpers throw
`NhAiMcpToolException` with `Code`, `FailureMessage`, `EvidenceReference` and
`PayloadJson`, or `NhAiMcpToolException<TPayload>` with the typed `Payload` when
the failure carried one. Catch the base type unless the typed payload is needed.

MCP annotation hints follow the governance effect. When a client-facing hint must
be more cautious than the effect, declare `ReadOnlyHint`, `DestructiveHint`,
`IdempotentHint` or `OpenWorldHint` on the tool. An override may only narrow: it
can publish `destructiveHint: true` for a consumer-authoritative mutation, but it
can never claim read-only, idempotent, non-destructive or closed-world behavior
the effect does not imply. The generator rejects a widening override with
`NHAI013`, runtime validation rejects hand-built widening descriptors, and the
audit record lists declared overrides in `AnnotationOverrides`. A hint never
changes approval, idempotency or verification.

Other libraries may register independently governed MCP tools through
`WithTools`, `WithToolsFromAssembly`, or manual `McpServerTool` services. Keep
their export names distinct from every NewHeap-managed export. Startup rejects
name collisions and SDK registrations whose metadata identifies them as a
NewHeap tool, because those form a second publication path. Only source-generated
`INhAiGeneratedToolCatalog` implementations and runtime catalogs that implement
`INhAiAttestedToolCatalog` and pass `NhAiToolCatalogAttestation` at startup, such as
the API bridge catalog, enter the NewHeap export path.

## Avoid

- Adding separate MCP methods that copy generated domain tool implementations.
- Maintaining application-specific lists of generated tool names solely to wrap
  input and unwrap `TaskResult<T>` responses.
- Registering a consumer-owned write through the MCP SDK because the generated
  export envelope does not match its wire contract; declare a flat export schema
  on the generated tool instead.
- Declaring a stronger governance effect only to change an MCP annotation hint;
  declare a narrowing hint override instead.
- Parsing failure text or message templates for a code; read the result code from
  `_meta` or the client exception.
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
content of the failed `CallToolResult`. Snapshot the flat text byte for byte with
and without a declared context, including a null property. Deny the invocation gate
and assert both flat and enveloped exports publish `{ code, message }`, the text
`code: message` and the code in `_meta`. Assert the client exception exposes the
code, message, evidence reference and typed payload, that a narrowing hint is
listed and audited, and that a widening hint fails with `NHAI013`. SPM-222 and SPM-240 are the executable
references.
