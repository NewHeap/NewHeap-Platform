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
    .UseAdminPolicy("app.assistant.admin")
    .UseDefaultApplicationContext(applicationContextAsset)
    .ConfigureMcp(mcp => mcp.AllowedHosts.Add("planning.example.com"))
    .AddAgent(agent)
    .AddBusinessAuditSink<ApplicationAssistantAuditSink>()
    .WithLimits(limits => limits.MaxToolCallsPerTurn = 8));

endpoints.MapNewHeapAssistant("/api/assistant");
```

Startup fails when the ASP.NET AI integration, the access or admin policy, an agent profile or
policy is missing, or when the invocation gate or a durable manager was replaced after
the assistant. `AddNewHeapAssistant` is idempotent. The assistant relies on the
registered `INhAiAuthenticatedInvocationContextResolver`; a replaced resolver is used
as is, and no tenant claim is required.

## Configuration

| Key | Default | Meaning |
|---|---|---|
| `NewHeap:AI:Assistant:Enabled` | `false` | Kill switch. Off: `GET status` returns `{ enabled: false, agents: [] }`, all other endpoints `404`. |
| `NewHeap:AI:Assistant:AccessPolicy` | `app.assistant.access` | Policy every endpoint requires, unless `UseAccessPolicy` sets one. |
| `NewHeap:AI:Assistant:AdminPolicy` | `app.assistant.admin` | Additional policy for `admin/*`, unless `UseAdminPolicy` sets one. |
| `NewHeap:AI:Assistant:Mcp:RequireHttps` | `true` | MCP servers must use https; plain http is allowed only to loopback in Development. |
| `NewHeap:AI:Assistant:Mcp:AllowedHosts` | empty (any public host) | Optional allow-list of MCP hosts. Link-local and metadata addresses are always blocked. |
| `NewHeap:AI:Assistant:Mcp:ForwardUserTokenHosts` | empty | Exact hosts that may receive the caller's token (`forward-user-token`). |
| `NewHeap:AI:Assistant:Mcp:ToolListCacheDuration` | `00:05:00` | Cache duration of remote tool lists. |
| `NewHeap:AI:Assistant:Mcp:ConnectTimeout` | `00:00:10` | Connect timeout for MCP servers. |

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
| `GET` / `PUT preferences` | the caller's style preferences |

Administration endpoints additionally require the admin policy:

| Method and path | Response |
|---|---|
| `GET` / `PUT context`, `GET context/versions` | application context; `PUT` takes `expectedVersion` |
| `GET tools` | local and bridge tools that selectors can match |
| `GET` / `POST agents`, `PUT` / `DELETE agents/{id}`, `POST agents/{id}/reset` | code and admin agents; updates take `expectedVersion`, code agents are reset instead of deleted |
| `GET` / `POST mcp-servers`, `PUT` / `DELETE mcp-servers/{id}` | MCP servers; secrets are write-only (`hasSecret`) |
| `POST mcp-servers/{id}/test`, `POST mcp-servers/{id}/sync` | connection test and tool sync; sync returns `502` when the server is unreachable or rejects the credentials |
| `GET mcp-servers/{id}/tools`, `PUT mcp-servers/{id}/tools/{remoteName}` | synced tools; activate them and set their effect |

Validation errors and blocked hosts return `400`, unknown objects `404` and stale
versions `409`. MCP secrets are protected with ASP.NET Data Protection; persist the key
ring across restarts and nodes.

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
