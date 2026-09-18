# NewHeap.Platform.AI.Chat

Provider-neutral assistant conversations, turn orchestration, approvals and durable
budget and idempotency ledgers for NewHeap AI. Hosts use it through
`NewHeap.Platform.AI.Chat.AspNet` (`AddNewHeapAssistant`, `MapNewHeapAssistant`) and
one storage package: `NewHeap.Platform.AI.Chat.SqlServer` or
`NewHeap.Platform.AI.Chat.PostgreSql`.

## What the package provides

- **Agents** — `NhAssistantAgentDefinition` binds an id and version, a chat profile,
  versioned instructions (`NhAiTextAsset`), tool selectors (exact tool ids or prefixes
  ending in `.*`), a maximum autonomy and an optional required authorization policy.
  `NhAiTextAsset.FromEmbeddedResource(assembly, resource, id, version)` loads
  instructions from an embedded resource with normalized line endings; it is a static
  extension member of this namespace.
- **Turns** — a turn runs inside the HTTP request on the NewHeap Agent Framework
  adapter. Tools are discovered for `NhAiToolExposure.Agent`, intersected with the
  agent's selectors and capped (`MaxToolsPerAgent`). Every tool call runs through the
  governed function and the shared `INhAiToolInvoker`; a function middleware persists
  and streams it without bypassing governance.
- **Approvals** — when the invoker requires approval, the assistant creates the exact
  `NhAiProposal` from the arguments the invoker evaluated and pauses the turn. The
  human decision resumes the same run: the same governed function runs again with the
  proposal and approval ids, validated by `INhAiApprovalValidator` against the stored
  evidence (proposal hash, actor and accountable owner, lifetime, decision by a
  different actor). The accountable owner comes from the registered
  `INhAiAuthenticatedInvocationContextResolver`, or the caller when it sets none.
- **Durable managers** — per-actor, per-UTC-day budgets (`DailyToolCallBudgetPerActor`,
  optional `DailyModelCallBudgetPerActor`) and idempotency leases with expiry takeover
  and fencing. Outside assistant turns, budget and evidence requests are delegated to
  the implementations registered before the assistant.
- **Audit** — a content-free relay records governed outcomes and forwards
  `NhAssistantAuditEvent` values (identifiers, versions, codes, timestamps) to
  `INhAssistantBusinessAuditSink` implementations.

## Storage

The library owns the `nhai` schema: `AssistantConversation`, `AssistantMessage`,
`AssistantToolInvocation`, `AssistantApproval`, `AssistantBudgetLedger`,
`AssistantIdempotencyLease`, `AssistantAgent`, `AssistantAgentMcpServer`,
`AssistantApplicationContext`, `AssistantMcpServer`, `AssistantMcpTool` and
`AssistantUserPreference`. Message parts are versioned JSON; tool arguments and
results are bounded to 64 KB and carry the tool's data classification and retention
category. Status transitions use compare-and-swap updates and a provider-neutral
concurrency stamp. Configure the schema and migrations through
`NhAssistantDbContextOptions`; `RunMigrations` is off by default.

## Administration and personalization

Agents added with `AddAgent` are code agents: they are upserted into `AssistantAgent`
at startup and stay the default. Administrators override, disable and reset them and
create their own agents; every change carries an expected version. MCP servers are
assigned per agent, and their enabled tools are imported per turn through
`INhAiMcpClientToolImporter` as `mcp.<server>.<tool>`, so approval, idempotency,
budget and audit apply unchanged. Synced tools start disabled as approval-required
mutations.

Each turn composes its instructions in order of authority: the fixed library rules,
the application context (`UseDefaultApplicationContext` seeds version 1 once), the
agent instructions and the user's style preferences. Preferences become fixed English
or Dutch lines; custom instructions are bounded, neutralized and treated as style
wishes only. The composed identity and hash flow into the invocation context and the
separate versions and hashes into `NhAssistantAuditEvent`. Every administration change
is a content-free audit event.

## Content boundaries

Conversation content is application data and stays in the `nhai` tables. It never
reaches logs, `INhAiAuditSink`, usage records or the business audit sink. The agent
pipeline receives no logger factory, because framework components log function
arguments and results at trace level. UI previews are bounded to 2,000 characters and
redacted for confidential and restricted tools.

## Limits and operations

`NhAssistantLimits` configures tool calls per turn, message length, turn timeout,
daily budgets, approval lifetime, offered tools, replayed history and lease duration.
`POST cancel` reaches turns in the same process only; an abandoned running turn is
taken over after its deadline. See the `nh-ai-assistant` guidance rule and sample
cases SPM-245, SPM-246, SPM-247, SPM-250, SPM-251 and SPM-252.
