# v-next

<!-- AI assistant and API bridge lanes: each lane writes only below its own heading. The coordinator regroups these sections into the per-package format before merge. -->

## Lane A

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
