# NewHeap.Platform.AI.AspNet.Common

ASP.NET Core integration for `NewHeap.Platform.AI.Common`. It contributes an
authenticated actor's active division and narrow capability grants only after
configured `IAuthorizationService` policies succeed. The browser-supplied active
division header is never treated as authorization by itself.

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
