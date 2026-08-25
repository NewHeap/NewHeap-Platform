# NewHeap.Platform.AI.Mcp

Official Model Context Protocol adapters for generated NewHeap AI catalogs. The
adapter asks `INhAiToolDiscoveryService` for actor-specific MCP-visible tools,
then wraps the same generated `AIFunction` delegates used locally. Every call
therefore still passes through `INhAiToolInvoker` and its fail-closed invocation
gate.

The package depends on the official `ModelContextProtocol.Core` SDK. It does not
configure an HTTP or stdio transport, authentication scheme, remote endpoint, or
credentials. The host owns transport and authorization composition.

External MCP servers are a separate, untrusted boundary. Discover their tools
with the official client and pass only reviewed entries to
`INhAiMcpClientToolImporter`. The import options require a local namespace and
explicit policies for effects, approval, authorization, capabilities and
execution bounds. Remote descriptions are never model-facing authority, schemas
are bounded before import, unlisted tools remain absent, imported tools cannot
be re-exported over MCP, and every imported call still runs through
`INhAiToolInvoker`.
