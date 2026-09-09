---
id: nh-ai-mcp-tools
title: "Expose generated AI tools through MCP"
area: backend
reference: ai-mcp-tools
summary: "Adapt an explicitly MCP-exposed generated NewHeap catalog through the official SDK without duplicating domain code or bypassing discovery and invocation authorization."
sample-cases: ["SPM-222"]
public-symbols: ["INhAiMcpToolAdapter", "WithNewHeapPlatformAITools"]
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

Other libraries may register independently governed MCP tools through
`WithTools`, `WithToolsFromAssembly`, or manual `McpServerTool` services. Keep
their export names distinct from every NewHeap-managed export. Startup rejects
name collisions and SDK registrations whose metadata identifies them as a
NewHeap tool, because those form a second publication path. Only source-generated
`INhAiGeneratedToolCatalog` implementations enter the NewHeap export path.

## Avoid

- Adding separate MCP methods that copy generated domain tool implementations.
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
fails at startup. SPM-222 is the executable reference.
