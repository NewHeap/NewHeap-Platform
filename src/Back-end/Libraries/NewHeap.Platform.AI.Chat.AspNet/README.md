# NewHeap.Platform.AI.Chat.AspNet

ASP.NET Core assistant endpoints with server-sent event streaming and approval
decisions for NewHeap AI.

## Registration

Register the application's AI services and `AddNewHeapPlatformAIAspNet` first, then
the assistant, and map the endpoints:

```csharp
services.AddNewHeapPlatformAIAspNet(ai => ai.AddActiveDivisionScope("app.active-division.project.view"));
services.AddNewHeapAssistant(assistant => assistant
    .UsePostgreSql(connectionString, options => { options.Schema = "nhai"; options.RunMigrations = true; })
    .UseAccessPolicy("app.assistant.access")
    .UseChatProfile("project-assistant-chat")
    .AddAgent(agent)
    .AddBusinessAuditSink<ApplicationAssistantAuditSink>()
    .WithLimits(limits => limits.MaxToolCallsPerTurn = 8));

endpoints.MapNewHeapAssistant("/api/assistant");
```

Startup fails when the ASP.NET AI integration, the access policy, an agent profile or
policy is missing, or when the invocation gate or a durable manager was replaced after
the assistant. `AddNewHeapAssistant` is idempotent. The assistant relies on the
registered `INhAiAuthenticatedInvocationContextResolver`; a replaced resolver is used
as is, and no tenant claim is required.

## Configuration

| Key | Default | Meaning |
|---|---|---|
| `NewHeap:AI:Assistant:Enabled` | `false` | Kill switch. Off: `GET status` returns `{ enabled: false, agents: [] }`, all other endpoints `404`. |
| `NewHeap:AI:Assistant:AccessPolicy` | `app.assistant.access` | Policy every endpoint requires, unless `UseAccessPolicy` sets one. |

## Endpoints

| Method and path | Response |
|---|---|
| `GET status` | `AssistantStatus` |
| `GET agents` | agents whose required policy the caller satisfies |
| `GET conversations?page=&itemsPerPage=` | `{ items, total }` |
| `POST conversations` `{ agentId, title? }` | `201` conversation |
| `GET conversations/{id}` | conversation with messages and pending approval |
| `DELETE conversations/{id}` | `204`; the conversation is archived and hidden |
| `POST conversations/{id}/messages` `{ text, clientMessageId }` | `text/event-stream`; `409` when the conversation is not idle |
| `POST conversations/{id}/approvals/{approvalId}/decide` `{ decision, expectedProposalHash, reason? }` | `text/event-stream` of the resumed turn |
| `POST conversations/{id}/cancel` | `202` |

JSON is camelCase through one source-generated `NhAssistantJsonSerializerContext`.
Errors outside the stream use `{ code, messageKey }` with `messageKey =
nh-assistant.errors.<code>`. Conversations are visible to their owner only; other
callers receive `404`.

## Streaming

Each event is written as `event: <name>`, `data: <json>` and an empty line:
`turn.started`, `message.delta`, `tool.started`, `tool.completed`,
`approval.required`, `turn.completed` or `error`. A `: keep-alive` comment follows
every 15 seconds without events, and the stream closes after `turn.completed` or
`error`. Response buffering is disabled and `X-Accel-Buffering: no` is set for
reverse proxies. A client disconnect cancels the turn and releases the conversation.
Browser clients use `fetch` streaming, because `EventSource` cannot send an
`Authorization` header.
