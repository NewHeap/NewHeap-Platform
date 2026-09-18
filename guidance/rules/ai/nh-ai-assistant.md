---
id: nh-ai-assistant
title: "Add a governed assistant with durable conversations and in-chat approvals"
area: backend
reference: ai-assistant
summary: "Register agents over existing governed tools with AddNewHeapAssistant, persist conversations in the library-owned nhai schema, stream turns as server-sent events and let the user approve exact NewHeap proposals in the chat."
sample-cases: ["SPM-245", "SPM-246", "SPM-247"]
public-symbols: ["AddNewHeapAssistant", "MapNewHeapAssistant", "NhAssistantBuilder", "NhAssistantAgentDefinition", "NhAssistantLimits", "NhAssistantOptions", "NhAssistantDbContextOptions", "UseSqlServer", "UsePostgreSql", "INhAssistantBusinessAuditSink", "NhAssistantAuditEvent", "INhAssistantTitleGenerator", "NhAssistantTextAssets", "NhAiScriptedChatClient"]
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
    .WithLimits(limits => { limits.MaxToolCallsPerTurn = 8; limits.DailyToolCallBudgetPerActor = 200; }));

endpoints.MapNewHeapAssistant("/api/assistant");
```

`NhAiTextAsset.FromEmbeddedResource` is a static extension member of
`NewHeap.Platform.AI.Chat`; keep that namespace in scope. The chat profile must
declare chat, function calling and streaming and permit the assistant execution
region (`local` by default, see `UseExecutionRegion`). Tool selectors are exact tool
ids or prefixes ending in `.*`; only agents with `Execute` autonomy are offered
mutating tools.

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

The library replaces `INhAiBudgetManager`, `INhAiIdempotencyManager` and
`INhAiApprovalEvidenceProvider` with durable implementations on its own tables.
Budget and evidence requests outside an assistant turn are delegated to the
implementations registered before `AddNewHeapAssistant`.

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

## Verification

Drive turns with `NhAiScriptedChatClient` against real SQL Server and PostgreSQL:
a read-only turn with one tool call, a mutation that pauses for approval and
resumes after approve, a reject that closes without executing, an expired
proposal, an exhausted daily budget, the tool-call limit, cancel and a disabled
flag. Assert that no prompt, argument, result or answer text reaches the audit,
usage and business sinks or logs, that approval is bound to the proposal hash and
owner, and that the SSE event and JSON property names match the contract.
SPM-245, SPM-246 and SPM-247 are the executable references.
