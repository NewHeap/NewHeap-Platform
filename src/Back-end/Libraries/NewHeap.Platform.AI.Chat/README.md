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
- **Presentation** — consumers may register `INhAssistantToolPresenter` implementations
  with `AddToolPresenter<T>()`. A presenter receives the exact governed arguments,
  invocation context and request culture and may return a localized name, summary and
  named fields. Output is bounded and stored with the approval; failures, time-outs,
  oversized output and confidential or restricted tools use a safe generic fallback
  without changing proposal evidence or rights.
- **Durable managers** — per-actor, per-UTC-day budgets (`DailyToolCallBudgetPerActor`,
  optional `DailyModelCallBudgetPerActor`) and idempotency leases with expiry takeover
  and fencing. Outside assistant turns, budget and evidence requests are delegated to
  the implementations registered before the assistant.
- **Agent runs** — `ConfigureAgent` lets an agent run on its own: started by an
  application event (`AddTrigger<TEvent>`) or by a person on a subject, executed as a
  background operation by `NewHeap.Platform.AI.Chat.AspNet`, in one thread per agent
  and subject whose access an `INhAiSubjectAccessResolver` (`AddSubjectType`) decides.
  Agent actions that change state become proposals under a named approval policy
  (`AddApprovalPolicy`): inline proposals pause the run, deferred proposals wait for
  people in the thread and the approval inbox and execute later as their own operation.
  Tools opt in to grants beyond the initiator's rights with `AllowAgentGrant`.
- **Subject types for people** — `AddSubjectType<TResolver>(type => type.Named(...)
  .NamedPlural(...).ForEntity<T>().OpenAt("/path/{id}"))` names a subject type per language,
  binds it to its entity and links to where the application shows a subject. A resolver that
  also implements `INhAiSubjectSearch` lets people find subjects by name; the platform keeps
  only what `ResolveAsync` lets the person read. Types starting with `nh-` are reserved.
- **What an agent works on** — `ConfigureAgent(id, run => run.WorksOn("project")
  .WorksOnApplication().RunPolicy("...").ViewPolicy("..."))` says which object types, all objects
  (`WorksOnAllObjects`) and/or the whole application an agent works on; triggers, starts, hand-offs
  and consultations on other types are refused with `assistant-agent-does-not-work-on`. An agent
  that says nothing works on all object types. `AddAgentPolicy(name, description)` lists the policies
  administrators may choose for agents created in the administration, and
  `Subject("project", e => ...)` lets startup check a trigger's type.
- **The whole application** — agents that work on the application run on the platform's own
  subject `nh-system/global` (`NhAiSystemSubjects`), or once per division with `PerDivision()` when
  the application lists its divisions (`INhDivisionDirectory`; NewHeap Identity lists its own). A
  schedule without `Subjects`, or a data schedule without a subject set, runs once per tick for the
  whole application. `UseSystemSubject(system => system.ReadPolicy("...").ApprovePolicy("..."))`
  decides who reads it and who decides its proposals.
- **Consultations** — `AllowConsultation(subjectType, ...)` in `ConfigureAgent` lets other
  assistants consult the agent about a subject through the generated tools
  `assistant.consult.{agentId}.{subjectType}` (an answer in the same turn) and, with
  `AllowRequests()`, `assistant.request.{agentId}.{subjectType}` (a run with the asker as
  initiator). The asking agent selects them like any tool. A consultation uses the asking
  person's rights only, reads the agent's memory, summaries and recent thread, writes no
  message into the thread and turns every change into a deferred proposal for the
  subject's approvers. `NhAssistantLimits.MaxConsultationDepth` and
  `MaxConsultationsPerActorPerDay` bound it; the consulted agent's daily budget includes
  its consultations.
- **Proposal feedback** — after a rejection, the person who decided a proposal and the
  subject's approvers may say what was wrong: a category from
  `NhAssistantProposalFeedbackCategories` and an optional text, stored in
  `AssistantProposalFeedback`. The next run on the subject starts from its recent
  rejections and their feedback (`MaxRejectionsInContext`, `RejectionContextWindow`).
  `AddProposalFeedbackSink` forwards each feedback to the application's analytics.
- **Audit** — a content-free relay records governed outcomes and forwards
  `NhAssistantAuditEvent` values (identifiers, versions, codes, timestamps) to
  `INhAssistantBusinessAuditSink` implementations.

## Storage

The library owns the `nhai` schema: `AssistantConversation`, `AssistantMessage`,
`AssistantToolInvocation`, `AssistantApproval`, `AssistantBudgetLedger`,
`AssistantIdempotencyLease`, `AssistantAgent`, `AssistantAgentMcpServer`,
`AssistantApplicationContext`, `AssistantMcpServer`, `AssistantMcpTool`,
`AssistantUserPreference`, and for agent runs `AssistantThread`, `AssistantRun`,
`AssistantQueuedInput` and `AssistantSubjectMember`. Conversations have a kind: `chat`
for a person's conversations and `thread` for agent threads, which never appear in a
person's conversation list. User messages keep their bounded page context in
`AssistantMessage.ClientContextJson`. Optional approval presentation is stored in
`AssistantApproval.PresentationJson`, so old rows remain valid and the selected culture
survives pause, reload and resume. Message parts are versioned JSON; tool arguments and
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

## Situational and page context

Every turn adds a "Situation" block with the date, weekday and time in the zone set by
`UseTimeZone` (UTC by default) and the facts of `INhAssistantTurnContextProvider`
implementations registered with `UseTurnContextProvider<T>()` (at most 20 facts and 2,000
characters). When the client sends a page context with the message, a "User's screen"
block lists its route, title and up to five entities. Both blocks follow the instructions,
are marked as data rather than instructions and are excluded from the prompt hash and the
approval binding. The page context is stored with the user message (`ClientContext`
migration) and reused when a turn resumes after an approval. A failing provider is logged
by exception type and skipped.

## Content boundaries

Conversation content is application data and stays in the `nhai` tables. It never
reaches logs, `INhAiAuditSink`, usage records or the business audit sink. The agent
pipeline receives no logger factory, because framework components log function
arguments and results at trace level. UI previews are bounded to 2,000 characters and
redacted for confidential and restricted tools.

## Limits and operations

`NhAssistantLimits` configures tool calls per turn, message length, turn timeout,
daily budgets, approval lifetime, offered tools, replayed history and lease duration.
When the tool-call limit or the daily tool budget stops the tool loop, one tool-free
model call answers from the results gathered so far; `turn.completed` keeps
`assistant-tool-call-limit-reached`. Replayed assistant messages carry a bounded
`<tool-call-summary>` of their tool calls (tool id, short argument preview, outcome;
never results), so a follow-up question knows what was already checked.
Chat turns are bound to their HTTP request: `POST cancel` reaches turns in the same
process only, and an abandoned running turn is taken over after its deadline. Work that
must outlive a request belongs in an agent run, which a background operation carries
across restarts and nodes. Model calls retry a transient provider failure twice with
backoff; a streamed answer is retried only before its first update. See the `nh-ai-assistant` guidance rule and sample
cases SPM-245, SPM-246, SPM-247, SPM-250, SPM-251 and SPM-252.
