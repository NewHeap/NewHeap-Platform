# v-next

## NewHeap.Platform.AI.Common

| Breaking change | Required action |
|---|---|
| None. Attested runtime catalogs (`INhAiAttestedToolCatalog`, `NhAiToolCatalogAttestation.Validate`) may now enter the MCP export path. | No action for existing consumers. |
| None. Governed functions accept tool arguments without the `input` envelope when `input` is the only schema property, and return the recoverable `ai-tool-input-invalid` result (`NhAiToolFailureCodes.InputInvalid`, audited, never executed) for an envelope mixed with other properties or arguments that cannot be bound. `NhAiGovernedAIFunction.Create` gains an overload with the catalog services, and the invoker logs unexpected exceptions content-free. | No action; regenerate tool catalogs by rebuilding with the updated generator so rejections are audited. |

## NewHeap.Platform.AI.Mcp

| Breaking change | Required action |
|---|---|
| None. `WithNewHeapPlatformAITools` accepts generated and attested catalogs and validates attested catalogs at startup. | No action for existing consumers. |

## NewHeap.Platform.AI.AspNet.Common

| Breaking change | Required action |
|---|---|
| None. `AddNewHeapPlatformAIAspNet` registers `INhAiCallerCredentialAccessor` (saved `access_token`, then the `Authorization: Bearer` header) for same-user delegated calls; the token never enters `NhAiInvocationContext`. | No action; register your own implementation before the call to replace it. |

## NewHeap.Platform.AI.AspNet.Mvc (new package)

`AddNewHeapPlatformAIMvcBridge` publishes policy-protected MVC actions as governed,
attested tools that run through `INhAiToolInvoker` and the API's own HTTP pipeline
as the calling user.

| Adoption note | Required action |
|---|---|
| The bridge requires `AddNewHeapPlatformAIAspNet`, a budget manager and an idempotency manager, and owns `INhAiToolDiscoveryPolicy`. | Configure your own discovery policy with `UseInnerDiscoveryPolicy` instead of `UseDiscoveryPolicy`. |
| DELETE, anonymous, upload and policy-less actions are never published; `IncludeDeleteActions(true)` fails at startup in this version. | Expose deletions through a curated tool with a verifier. |
| `EnableGateway` adds the read-only gateway tools `search-resources`, `describe-resource`, `query` and `get` over the read-only bridge actions per resource; `INhAiBridgeConventions` gains `DescribeQuery` with a default implementation that describes no fields. | No action; override `DescribeQuery` to have filter and order keys validated before the HTTP call. |
| Bridge tools share the governed-function argument handling: flat arguments run once, a mixed `input` shape returns `ai-tool-input-invalid` before any HTTP call, and unknown flat properties still fail as `api-bridge-validation`. | No action. |

## NewHeap.Platform.AI.Chat, NewHeap.Platform.AI.Chat.SqlServer, NewHeap.Platform.AI.Chat.PostgreSql, NewHeap.Platform.AI.Chat.AspNet (new packages)

`AddNewHeapAssistant` and `MapNewHeapAssistant` add durable assistant conversations,
agent turns streamed as server-sent events, in-chat approval of exact NewHeap
proposals and durable budget and idempotency ledgers. Administrators manage agents,
MCP servers and the application context through `admin/*`; users set style
preferences through `preferences`. The assistant replaces `INhAiBudgetManager`,
`INhAiIdempotencyManager` and `INhAiApprovalEvidenceProvider` in hosts that register
it; budget and evidence requests outside assistant turns are delegated to the
previously registered implementations.

| Adoption note | Required action |
|---|---|
| The assistant owns the `nhai` schema with its own SQL Server and PostgreSQL migrations (`Initial`, `AdminAndPreferences`). | Apply them with `RunMigrations = true` or as a deployment step before enabling `NewHeap:AI:Assistant:Enabled`. |
| The assistant decorates the ASP.NET AI invocation gate and validates its registration at startup. | Call `AddNewHeapAssistant` after `AddNewHeapPlatformAIAspNet` and the application's own AI registrations. |
| Startup requires the admin policy (`app.assistant.admin` unless configured). | Register the policy, or call `UseAdminPolicy` with an existing policy. |
| Turn instructions combine the library rules, the application context, the agent instructions and the user's preferences; pending approvals are bound to those instructions. | Approvals pending during a context or preference change must be decided again. |
| MCP secrets are protected with ASP.NET Data Protection. | Persist the Data Protection key ring across restarts and nodes; otherwise administrators must enter the secrets again. |
| Unexpected tool exceptions in a turn are logged as content-free warnings (tool id, version, turn id, exception type); malformed tool arguments return `ai-tool-input-invalid` as the tool-call `resultCode` so the model can retry. | No action. |

## NewHeap.Platform.AI.Test

| Breaking change | Required action |
|---|---|
| None. Added `NhAiScriptedChatClient` for scripted text and function-call responses in agent and assistant tests. | No action. |

## @newheap/platform-ai-chat (new package)

Angular assistant panel (`provideNhAssistant`, `nh-assistant-launcher`,
`nh-assistant-panel`, `nh-assistant-preferences` and building blocks), the
administration page in `@newheap/platform-ai-chat/admin` and a scripted mock API in
`@newheap/platform-ai-chat/testing`.

| Adoption note | Required action |
|---|---|
| Peer dependencies: Angular 20.3 (`common`, `core`, `router`), `@angular/cdk` 20.2, `@ngx-translate/core` 17, `marked` 18, `dompurify` 3.4. | Install the peers. |
| The panel is a CDK overlay. | Load `@angular/cdk/overlay-prebuilt.css` in the host. |
