# NewHeap.Platform.AI.AspNet.Common

ASP.NET Core integration for `NewHeap.Platform.AI.Common`. It contributes an
authenticated actor's active division and narrow capability grants only after
configured `IAuthorizationService` policies succeed. The browser-supplied active
division header is never treated as authorization by itself.

OIDC/JWT hosts that disable inbound claim mapping configure
`UseAuthenticatedClaims` with the exact expected issuer and their `iss`, `sub`,
and tenant claim types. `INhAiAuthenticatedInvocationContextResolver` rejects
missing or duplicate authority claims, projects only configured scalar scopes
and scope-to-capability mappings, and links every resolution to request
cancellation. The invocation context keeps issuer, subject, tenant, and a
collision-resistant actor ID separate.

ASP.NET MCP hosts call `WithNewHeapPlatformAITools()` on the official
`IMcpServerBuilder`. List and call requests resolve this authenticated context
per request, use the generated catalog for both discovery and invocation, and
return authorization failures as structured MCP tool errors. Startup rejects
SDK registrations identified as NewHeap tools and wire-name collisions,
while independently governed tools from other libraries may use `WithTools`,
assembly scanning, or manual `McpServerTool` registration.

The integration adds bounded IDs and correlation metadata to the provider-neutral
invocation context. It does not copy a `ClaimsPrincipal`, token, cookie, request
body, prompt, or credential into that context.

`INhAiBackgroundOperationRunAdapter` maps a durable operation ID, attempt,
idempotency key and fencing token into a non-human AI invocation. It persists
only versioned checkpoint references, delegates approval waits to the general
background-operation suspension contract, and leaves authoritative artifacts
and conversation content application-owned.

`INhAiBackgroundOperationIngestionAdapter` binds ingestion to that durable run,
uses the operation idempotency key, and stores a content-free completion
checkpoint. Re-entry returns the checkpointed result without re-reading the
source or generating embeddings again; a mismatched document or collection
fails closed.
