# v-next

<!-- AI assistant and API bridge lanes: each lane writes only below its own heading. The coordinator regroups these sections into the per-package format before merge. -->

## Lane A

### NewHeap.Platform.AI.Common, NewHeap.Platform.AI.Mcp, NewHeap.Platform.AI.AspNet.Common

| Change | Required action |
| --- | --- |
| AI.Common: attested runtime catalogs (`INhAiAttestedToolCatalog`, `NhAiToolCatalogAttestation.Validate`) may enter the MCP export path; `WithNewHeapPlatformAITools` validates them at startup. | No action for existing consumers. |
| AI.AspNet.Common: `AddNewHeapPlatformAIAspNet` registers `INhAiCallerCredentialAccessor` (saved `access_token`, then the `Authorization: Bearer` header) for same-user delegated calls; the token never enters `NhAiInvocationContext`. | No action; register your own implementation before the call to replace it. |

### NewHeap.Platform.AI.AspNet.Mvc (new package)

| Change | Required action |
| --- | --- |
| New `AddNewHeapPlatformAIMvcBridge` publishes policy-protected MVC actions as governed, attested tools that run through `INhAiToolInvoker` and the API's own HTTP pipeline as the calling user. | Opt in per API; register `AddNewHeapPlatformAIAspNet`, a budget and an idempotency manager, then configure your own discovery policy with `UseInnerDiscoveryPolicy` instead of `UseDiscoveryPolicy`. |
| The bridge never publishes DELETE, anonymous, upload or policy-less actions; `IncludeDeleteActions(true)` fails at startup in v1. | Expose deletions through a curated tool with a verifier. |

## Lane B

### NewHeap.Platform.AI.Chat, NewHeap.Platform.AI.Chat.SqlServer, NewHeap.Platform.AI.Chat.PostgreSql, NewHeap.Platform.AI.Chat.AspNet (new)

New packages; no breaking change to existing packages. `AddNewHeapAssistant` and
`MapNewHeapAssistant` add durable assistant conversations, agent turns streamed as
server-sent events, in-chat approval of exact NewHeap proposals and durable budget
and idempotency ledgers. The assistant replaces `INhAiBudgetManager`,
`INhAiIdempotencyManager` and `INhAiApprovalEvidenceProvider` in hosts that register
it; budget and evidence requests outside assistant turns are delegated to the
previously registered implementations.

| Adoption note | Required action |
|---|---|
| The assistant owns the `nhai` schema with its own SQL Server and PostgreSQL migrations. | Apply them with `RunMigrations = true` or as a deployment step before enabling `NewHeap:AI:Assistant:Enabled`. |
| The assistant decorates the ASP.NET AI invocation gate and validates its registration at startup. | Call `AddNewHeapAssistant` after `AddNewHeapPlatformAIAspNet` and the application's own AI registrations. |

### NewHeap.Platform.AI.Test

Added `NhAiScriptedChatClient` for scripted text and function-call responses in
agent and assistant tests. No breaking change.

## Lane C

### @newheap/platform-ai-chat

New package. Angular assistant panel (`provideNhAssistant`, `nh-assistant-launcher`, `nh-assistant-panel` and building blocks) for the assistant API, with a scripted mock API in `@newheap/platform-ai-chat/testing`. Peer dependencies: Angular 20.3, `@angular/cdk` 20.2, `@ngx-translate/core` 17, `marked` 18, `dompurify` 3.4. Hosts load `@angular/cdk/overlay-prebuilt.css`. No breaking changes; existing packages are unaffected.

Administration and preferences: `nh-assistant-preferences` in the panel, the administration page `nh-assistant-admin` in the new entry point `@newheap/platform-ai-chat/admin`, `NhAssistantAdminApiService`, `NhAssistantConfig.adminRoute` and `AssistantStatus.canAdminister`, with matching mock endpoints in `/testing`. `@angular/router` is a new peer dependency. No breaking changes.
