# v-next

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
| `INhAiBridgeConventions` gains `IsCollectionAction` (default: the action binds a NewHeap collection request model); the gateway uses it to choose between `query` and `get`, and for recognized actions without a model the default conventions add the collection fragment to the input schema and query string. | No action; override `IsCollectionAction` for list endpoints that read `page`, `itemsPerPage`, `search`, `orderBy` and `filter` from the query string themselves. |
| Canonical NewHeap collection request/result endpoints now publish collection fields and query encoding without custom conventions; legacy contracts can register `INhAiBridgeCollectionContractProvider`. | Remove duplicated collection reflection/operator mappings and register only a narrow provider when the wire contract differs. |
| Body serialization is selectable through `INhAiBridgeBodySerializer`; the optional `NhAiMvcNewtonsoftJsonBodySerializer` uses the current MVC Newtonsoft settings. | Use `UseBodySerializer<NhAiMvcNewtonsoftJsonBodySerializer>()` instead of replacing all bridge conventions. |
| Generated export names longer than 64 characters are deterministically compacted and remain mapped in the manifest. | Remove consumer-specific export-name compaction; existing valid names remain unchanged. |
| Discovery now rejects an invocation context whose actor/accountable owner does not match the signed-in principal. | Pass the context built for the current authenticated principal; do not reuse another actor's context. |
| Trusted query bindings and localized gateway resource presentation are composable bridge options. | Resolve trusted values from invocation scope and keep resource ids invariant while moving presentation text to consumer resources. |
| Gateway `query` and `get` now shape successful results before the size bound: without `fields` items are compacted (nulls dropped, nested objects reduced to identifying fields), `fields` projects result fields including one dotted level, and `describe-resource` publishes `resultShaping`. | No action; pass `fields` when the model needs nested values that compaction drops. |
| An oversized gateway result keeps whole items and returns `truncation` (`totalCount`, `resultCount`, `returnedCount`, `suggestedItemsPerPage`, `suggestedFields`) instead of a raw `bodyText` fragment; `NhAiBridgeResponse` gains the optional `Truncation` property. | No action; read `truncation` instead of `bodyText` for gateway results. |
| Gateway `query` accepts `countOnly`; `INhAiBridgeConventions.BuildCountRequest` and `INhAiBridgeCollectionContractProvider.TryEncodeCountQuery` have default implementations that request the first page with one item. | Optional: implement `TryEncodeCountQuery` when the API supports a count-only flag. |
| `NhAiMvcBridgeGatewayBuilder.RedactResultFields` removes matching result fields from gateway results at every depth and from `describe-resource`, `fields`, filters and ordering; `UseMaxResponseBytes` bounds the bytes read for shaping (default 4 MiB). | Redact personal data such as `*email*`; do not also expose the direct bridge tools of redacted resources. |

## NewHeap.Platform.AI.Common and NewHeap.Platform.AI.AspNet.Common

`NhAiInMemoryBudgetManager` provides bounded process-local per-actor budgets for
samples and local/internal deployments; its state is explicitly non-durable.
Authenticated ASP.NET context projection now supports explicit tenantless and
single-tenant modes, and configured capability mappings can combine repeated
permission claims while authority claims continue to reject duplicates.

## NewHeap.Platform.AI.Chat, NewHeap.Platform.AI.Chat.SqlServer, NewHeap.Platform.AI.Chat.PostgreSql, NewHeap.Platform.AI.Chat.AspNet (new packages)

`AddNewHeapAssistant` and `MapNewHeapAssistant` add durable assistant conversations,
agent turns streamed as server-sent events, in-chat approval of exact NewHeap
proposals and durable budget and idempotency ledgers. Administrators manage agents,
MCP servers and the application context through `admin/*`; users set style
preferences through `preferences`. Every turn gets a "Situation" data block (date and
time in the zone set with `UseTimeZone`, plus facts from `INhAssistantTurnContextProvider`
implementations registered with `UseTurnContextProvider<T>()`) and, when `POST messages`
carries an optional `clientContext`, a "User's screen" block. The assistant replaces `INhAiBudgetManager`,
`INhAiIdempotencyManager` and `INhAiApprovalEvidenceProvider` in hosts that register
it; budget and evidence requests outside assistant turns are delegated to the
previously registered implementations.

| Adoption note | Required action |
|---|---|
| The assistant owns the `nhai` schema with its own SQL Server and PostgreSQL migrations (`Initial`, `AdminAndPreferences`, `ClientContext`, `ApprovalPresentation`). | Apply them with `RunMigrations = true` or as a deployment step before enabling `NewHeap:AI:Assistant:Enabled`. |
| The assistant decorates the ASP.NET AI invocation gate and validates its registration at startup. | Call `AddNewHeapAssistant` after `AddNewHeapPlatformAIAspNet` and the application's own AI registrations. |
| Startup requires the admin policy (`app.assistant.admin` unless configured). | Register the policy, or call `UseAdminPolicy` with an existing policy. |
| Turn instructions combine the library rules, the application context, the agent instructions and the user's preferences; pending approvals are bound to those instructions. | Approvals pending during a context or preference change must be decided again. |
| Situational and page context are optional and non-breaking: without providers a turn gets only the date and time (UTC unless `UseTimeZone` is set), and without `clientContext` no screen block. Neither block changes the prompt hash, so pending approvals stay valid. | No action; add providers or send `clientContext` to use them. |
| A provider error inside the model stream (for example exhausted credits or quota) now ends the turn as failed with `assistant-model-unavailable` instead of an empty completed turn; only the provider error code is logged. | No action. |
| MCP secrets are protected with ASP.NET Data Protection. | Persist the Data Protection key ring across restarts and nodes; otherwise administrators must enter the secrets again. |
| Unexpected tool exceptions in a turn are logged as content-free warnings (tool id, version, turn id, exception type); malformed tool arguments return `ai-tool-input-invalid` as the tool-call `resultCode` so the model can retry. | No action. |
| `AddToolPresenter<T>()` optionally adds bounded, localized tool names and approval explanations from the exact governed arguments and context; presentation is persisted outside proposal evidence and safely falls back on failure, timeout, oversize output, sensitive classification or old rows. | Optional: implement `INhAssistantToolPresenter`, add request localization and apply the new `ApprovalPresentation` migration. |

## @newheap/platform-ai-chat (new package)

Angular assistant panel (`provideNhAssistant`, `nh-assistant-launcher`,
`nh-assistant-panel`, `nh-assistant-preferences` and building blocks), the
administration page in `@newheap/platform-ai-chat/admin` and a scripted mock API in
`@newheap/platform-ai-chat/testing`.

| Adoption note | Required action |
|---|---|
| Peer dependencies: Angular 20.3 (`common`, `core`, `router`), `@angular/cdk` 20.2, `@ngx-translate/core` 17, `marked` 18, `dompurify` 3.4. | Install the peers. |
| The panel is a CDK overlay. | Load `@angular/cdk/overlay-prebuilt.css` in the host. |
| Messages can carry page context: `NhAssistantConfig.getPageContext` returns `NhAssistantClientContext` (`route`, `title`, `entities`), sent as `clientContext` and truncated to the contract limits; a chip above the message box shows it and lets the user leave it out of the next message. | Optional: provide `getPageContext` from a service that entity pages set and clear. |
| The composer remains editable while running or awaiting approval, blocked Enter preserves the draft, and approval cards prefer optional trusted presentation while technical ids and previews stay collapsed. | No action; custom layouts should pass `sendDisabled` separately from `disabled` and treat `approval.presentation` as optional. |
