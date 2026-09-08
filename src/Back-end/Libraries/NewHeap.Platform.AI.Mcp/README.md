# NewHeap.Platform.AI.Mcp

Official provider-neutral Model Context Protocol adapters for generated NewHeap
AI catalogs. The adapter asks `INhAiToolDiscoveryService` for context-specific
MCP-visible tools, then wraps the same generated `AIFunction` delegates used
locally. Every call therefore still passes through `INhAiToolInvoker` and its
fail-closed invocation gate.

The package depends on the official provider-neutral `ModelContextProtocol.Core`
SDK. ASP.NET servers add `NewHeap.Platform.AI.AspNet.Common` and call
`WithNewHeapPlatformAITools()` on the official `IMcpServerBuilder`; that
request-bound integration owns context resolution. Other libraries may also use
`WithTools`, `WithToolsFromAssembly`, or manually registered `McpServerTool`
instances when their wire names remain distinct. Startup rejects only name
collisions and SDK registrations whose metadata identifies them as NewHeap
tools. The host still owns transport, authentication, endpoint, and
credentials. Only generated catalogs enter the NewHeap export path.

External MCP servers are a separate, untrusted boundary. Discover their tools
with the official client and pass only reviewed entries to
`INhAiMcpClientToolImporter`. The import options require a local namespace and
explicit policies for effects, approval, authorization, capabilities and
execution bounds. Remote descriptions are never model-facing authority, schemas
are bounded before import, unlisted tools remain absent, imported tools cannot
be re-exported over MCP, and every imported call still runs through
`INhAiToolInvoker`.
