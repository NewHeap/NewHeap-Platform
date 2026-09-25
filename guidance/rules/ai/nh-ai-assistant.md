---
id: nh-ai-assistant
title: "Add a governed assistant with durable conversations and in-chat approvals"
area: backend
reference: ai-assistant
summary: "Register agents over existing governed tools with AddNewHeapAssistant, persist conversations in the library-owned nhai schema, stream turns as server-sent events, let the user approve exact NewHeap proposals in the chat, and let administrators manage agents, MCP servers and the application context while users set style preferences."
sample-cases: ["SPM-245", "SPM-246", "SPM-247", "SPM-249", "SPM-250", "SPM-251", "SPM-252", "SPM-254"]
public-symbols: ["AddNewHeapAssistant", "MapNewHeapAssistant", "NhAssistantBuilder", "NhAssistantAgentDefinition", "NhAssistantLimits", "NhAssistantOptions", "NhAssistantDbContextOptions", "UseSqlServer", "UsePostgreSql", "INhAssistantBusinessAuditSink", "NhAssistantAuditEvent", "INhAssistantTitleGenerator", "NhAssistantTextAssets", "NhAiScriptedChatClient", "UseAdminPolicy", "UseDefaultApplicationContext", "ConfigureMcp", "NhAssistantMcpOptions", "NhAssistantAuditEventKind", "INhAssistantTurnContextProvider", "NhAssistantContextFact", "UseTurnContextProvider", "UseTimeZone", "INhAssistantToolPresenter", "NhAssistantApprovalPresentation", "NhAssistantPresentationField", "AddToolPresenter"]
skills: ["newheap-backend-development"]
providers: ["sql-server", "postgresql"]
risk: high
---
## Preferred approach

Add `NewHeap.Platform.AI.Chat.AspNet` and exactly one storage package,
`NewHeap.Platform.AI.Chat.SqlServer` or `NewHeap.Platform.AI.Chat.PostgreSql`.
Register the application's own AI services first (chat profiles, generated tool
catalogs, discovery policy, verifiers), then `AddNewHeapPlatformAIAspNet`, and call
`AddNewHeapAssistant` last:

```csharp
services.AddNewHeapAssistant(assistant => assistant
    .UsePostgreSql(connectionString, options => { options.Schema = "nhai"; options.RunMigrations = true; })
    .UseAccessPolicy("app.assistant.access")
    .UseChatProfile("project-assistant-chat")
    .AddAgent(new NhAssistantAgentDefinition(
        Id: "project-assistant", Version: 1,
        DisplayNameKey: "nh-assistant.agents.project-assistant.name",
        DescriptionKey: "nh-assistant.agents.project-assistant.description",
        ProfileName: "project-assistant-chat",
        Instructions: NhAiTextAsset.FromEmbeddedResource(typeof(Agents).Assembly, "App.Agents.project-assistant.md", "project-assistant", 1),
        ToolSelectors: ["projects.*"],
        Autonomy: NhAiAutonomyLevel.Execute,
        RequiredPolicy: "app.project.view"))
    .AddBusinessAuditSink<ApplicationAssistantAuditSink>()
    .AddToolPresenter<ApplicationAssistantToolPresenter>()
    .WithLimits(limits => { limits.MaxToolCallsPerTurn = 8; limits.DailyToolCallBudgetPerActor = 200; }));

endpoints.MapNewHeapAssistant("/api/assistant");
```

`NhAiTextAsset.FromEmbeddedResource` is a static extension member of
`NewHeap.Platform.AI.Chat`; keep that namespace in scope. The chat profile must
declare chat, function calling and streaming and permit the assistant execution
region (`local` by default, see `UseExecutionRegion`). Tool selectors are exact tool
ids or prefixes ending in `.*`; only agents with `Execute` autonomy are offered
mutating tools. An agent may list at most `NhAssistantLimits.MaxToolSelectorsPerAgent`
selectors (128 by default) for code-defined and administrator-managed agents alike;
the application decides that number through `WithLimits`. Code agents are checked at
host start, administrator input on save. The selector list must also fit
`NhAssistantLimits.MaxStoredToolSelectorCharacters` (8,000 characters of JSON).
Prefer prefix selectors over long lists of exact ids, and remember that
`MaxToolsPerAgent` (64 by default) still bounds how many tools one turn offers.

The kill switch is `NewHeap:AI:Assistant:Enabled` (default `false`). When it is
off, `GET status` reports `enabled: false` without agents and every other endpoint
returns `404`. Every endpoint requires the access policy
(`NewHeap:AI:Assistant:AccessPolicy`, default `app.assistant.access`); an agent's
`RequiredPolicy` is checked when the agent is listed, when a conversation is created
and before every turn.

A turn runs inside the HTTP request and streams `turn.started`, `message.delta`,
`tool.started`, `tool.completed`, `approval.required`, `turn.completed` or `error`,
with a `: keep-alive` comment every 15 seconds. A client disconnect or `POST cancel`
cancels the turn in the same process. Tools run as the signed-in user through the
application's invocation gate: the assistant decorates that gate so the agent acts
as a non-human actor on behalf of the accountable owner taken from the registered
`INhAiAuthenticatedInvocationContextResolver`, with the turn id as run id and the
instruction version and hash as prompt identity.

When the shared invoker requires approval, the assistant creates the exact
`NhAiProposal` from the arguments the invoker evaluated, stores it with a pending
approval, streams `approval.required` and pauses. The user decides with the
expected proposal hash. On approve, the same governed function runs again through
the invoker with the proposal and approval ids bound to the context, a durable
idempotency lease keyed by the proposal and the tool's verifier; on reject, the
model receives a rejected result and may close with one message. Expired
proposals end with `assistant-approval-expired`.

Use `AddToolPresenter<T>()` when a consumer can explain a tool with localized domain
names. The presenter receives the exact governed arguments, invocation context and
request culture. Resolve display data again inside the authorized scope, return a short
`NhAssistantApprovalPresentation`, and keep `NhAiToolDescriptor.Description` as English
model metadata. Presentation is bounded, persisted and optional; it never enters the
proposal hash or grants access. Failure, timeout, oversized output, confidential or
restricted classification, old stored data and an unhandled tool all use the safe
fallback. Never log presenter text, arguments or lookup results.

The library replaces `INhAiBudgetManager`, `INhAiIdempotencyManager` and
`INhAiApprovalEvidenceProvider` with durable implementations on its own tables.
Budget and evidence requests outside an assistant turn are delegated to the
implementations registered before `AddNewHeapAssistant`.

## Administration and personalization

Register an admin policy with `UseAdminPolicy` (or `NewHeap:AI:Assistant:AdminPolicy`,
default `app.assistant.admin`); startup fails when it does not exist, because
`MapNewHeapAssistant` also maps the `admin/*` endpoints behind the access and admin
policies. `GET status` reports `canAdminister`. Errors of `preferences` and `admin/*`
carry `{ code, messageKey, errors? }`: `assistant-validation` (`400`, camelCase field
keys in `errors`), `assistant-forbidden` (`403`) and specific `*-not-found` codes (`404`).

- **Agents.** Agents added with `AddAgent` are code agents: they are upserted into
  `AssistantAgent` at startup and stay the default. Administrators override, disable
  and reset them (the source stays `code`) and create their own agents. Every change
  carries `expectedVersion`; a stale version returns `409`. Tool selectors apply to
  local and bridge tools; MCP tools are assigned per server with `mcpServerIds`.
- **Application context.** `UseDefaultApplicationContext` seeds version 1 once; a
  changed seed never replaces an existing context. Administrators edit it with
  `expectedVersion`, and the history is kept.
- **Instructions.** Each turn composes, in order of authority, the fixed library
  rules, the application context, the agent instructions and the user's style
  preferences. Preferences (`style`, `addressForm`, `responseLength`) become fixed
  English or Dutch lines chosen from `Accept-Language`; custom instructions are
  bounded, neutralized and treated as style wishes only. The identity
  (`default@<version>+<asset>@<version>+preferences@<hash>`) and the SHA-256 of the
  composed text flow into the invocation context; the separate versions and hashes
  flow into `NhAssistantAuditEvent`. Editing the context or preferences while an
  approval is pending invalidates that approval.
- **MCP servers.** Configure `ConfigureMcp`: `RequireHttps` (plain http only to
  loopback in Development), optional `AllowedHosts`, exact `ForwardUserTokenHosts`,
  `ToolListCacheDuration` and `ConnectTimeout`. Secrets for `bearer` and `api-key`
  are protected with ASP.NET Data Protection and never returned (`hasSecret`), logged
  or audited; `forward-user-token` uses `INhAiCallerCredentialAccessor` only for
  allow-listed hosts. Synced tools start disabled as approval-required mutations;
  administrators activate them and may mark them read-only, and a changed input
  schema disables a tool again. Enabled tools of the servers assigned to an agent are
  imported per turn with `INhAiMcpClientToolImporter` (`mcp.<server>.<tool>`), so
  approval, idempotency, budget and audit apply unchanged. An unreachable server is
  skipped for the turn.
- Every administration change is a content-free audit event
  (`AdminContextUpdated`, `AdminAgent*`, `AdminMcpServer*`, `AdminMcpToolUpdated`).

Tool calls whose arguments arrive without the `input` envelope still run through the
governed function once. An envelope mixed with other properties, or arguments that
cannot be bound, completes the tool call with `resultCode` `ai-tool-input-invalid` and
a message that names only the unexpected property names, so the model can retry in
the same turn. Unexpected tool exceptions complete with `ai-tool-failed` and are
logged as warnings with tool ID, version, turn ID and exception type only.

When a turn reaches `MaxToolCallsPerTurn`, or the daily tool budget refuses a call, the
assistant does not end silently: it makes one more model call with tool choice `none`,
the tool results gathered so far and an instruction to answer with what is known and name
what is missing, and streams that answer as normal `message.delta`. `turn.completed` still
carries `assistant-tool-call-limit-reached` (an exhausted budget still ends with the
`assistant-budget-exhausted` `error` event) so the UI can show a notice. Any tool call in
the closing answer is refused without running. Follow-up turns replay each earlier
assistant message with a `<tool-call-summary>` block marked as data: at most ten lines of
tool id, version, an argument preview of 160 characters (redacted for confidential and
restricted tools), status, result code and a truncation flag. Results are never replayed
or logged, and the summary counts against the history budget.

## Situational and page context

Give the model the moment and the user without a tool call. `UseTimeZone("Europe/Amsterdam")`
sets the zone of the date, weekday and time the library adds to every turn (UTC by
default). Implement `INhAssistantTurnContextProvider` for short facts such as the user's
name, roles or active division, built from the authenticated context and the
application's own data only, and register it with `UseTurnContextProvider<T>()`; several
providers are allowed. Facts are cut off at 20 per turn, 2,000 characters in total, 60
characters per label and 300 per value. A failing provider is skipped for the turn and
logged with its exception type only.

Clients send the page they show as `clientContext` with `POST messages`: `route` (200
characters), optional `title` (120) and at most five `entities` (`type` in dash-case, `id`
of at most 64 characters, optional `label` of 120). The server validates again, cuts off
long text and drops a value of the wrong shape without answering `400`. The context is
stored with the user message and used again when the turn resumes after an approval.

Both arrive as the "Situation" and "User's screen" blocks after the composed instructions,
marked as data, not instructions. They are not part of the
prompt hash or the approval binding, so another moment or page never invalidates a
pending approval. Audit events carry only `ContextFactCount`, `PageEntityCount` and
`HadPageContext`.

## Avoid

- Calling `AddNewHeapAssistant` before `AddNewHeapPlatformAIAspNet` or replacing the
  invocation gate, budget, idempotency or evidence manager afterwards; startup fails.
- Registering or replacing `INhAiToolDiscoveryPolicy` for the assistant; discovery
  stays owned by the application or the API bridge.
- Adding agent-specific copies of tools to bypass approval, or approving on behalf
  of the user from code.
- Writing prompts, messages, tool arguments or results to logs, `INhAiAuditSink`,
  usage records or `INhAssistantBusinessAuditSink`; those receive identifiers,
  versions, codes and timestamps only.
- Applying the library migrations to a shared database without an explicit
  deployment decision; `RunMigrations` is off by default.
- Relying on `POST cancel` across nodes; it reaches turns in the same process, and
  an abandoned running turn is recovered after its deadline.
- Trusting remote MCP annotations such as `readOnlyHint`; they are hints for the
  administrator only. Mark a remote tool read-only only after reviewing it.
- Forwarding user tokens to hosts outside `ForwardUserTokenHosts`, or allowing plain
  http, link-local or metadata addresses for MCP servers.
- Putting permissions, approval rules or scope decisions in the application context or
  the preferences; they explain the domain and shape style only.
- Deciding access from page context or provider facts. Entity ids on the user's screen are
  search hints; authorization and division scope stay with the tools and the gate.
- Putting secrets, tokens or model input into provider facts.

## Verification

Drive turns with `NhAiScriptedChatClient` against real SQL Server and PostgreSQL:
a read-only turn with one tool call, a mutation that pauses for approval and
resumes after approve, a reject that closes without executing, an expired
proposal, an exhausted daily budget, the tool-call limit, cancel and a disabled
flag. At the tool-call limit, assert a closing answer from a request with tool choice
`none` that carries every call's result, no further tool execution, and a follow-up
turn whose history holds the tool-call summary but no result content. Send one call with flat arguments and one with `input` mixed with another
property, and assert the first succeeds and the second returns `ai-tool-input-invalid`
before a corrected retry succeeds. Assert that no prompt, argument, result or answer text reaches the audit,
usage and business sinks or logs, that approval is bound to the proposal hash and
owner, and that the SSE event and JSON property names match the contract.
For administration, assert version conflicts, the admin policy, the seed behavior and
the composed instruction order with an injection attempt in the custom instructions.
For situational and page context, assert the block order and markers, the limits, that
an invalid `clientContext` is ignored, that a failing provider does not stop the turn,
that the prompt hash and a pending approval survive another page, and that facts and
page text never reach audit or logs (SPM-254).
For MCP, use an official SDK server (in memory or Streamable HTTP): tools are off after
sync and visible only to the assigned agent after activation, mutations pause for
approval, schema changes disable tools, blocked hosts are rejected and secrets are
absent from responses, logs and audit. SPM-245, SPM-246, SPM-247, SPM-250, SPM-251 and
SPM-252 and SPM-254 are the executable references. SPM-249 is the end-to-end reference: one agent
over the API bridge and curated tools, an administrator agent with an MCP tool, and a
viewer who is offered no mutating tools.
