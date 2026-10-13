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
    .UseTimeZone("Europe/Amsterdam")
    .UseTurnContextProvider<ApplicationTurnContextProvider>()
    .ConfigureMcp(mcp => mcp.AllowedHosts.Add("planning.example.com"))
    .AddAgent(agent)
    .AddToolPresenter<ApplicationAssistantToolPresenter>()
    .AddBusinessAuditSink<ApplicationAssistantAuditSink>()
    .WithLimits(limits => limits.MaxToolCallsPerTurn = 8));

endpoints.MapNewHeapAssistant("/api/assistant");
```

Startup fails when the ASP.NET AI integration, the access or admin policy, an agent profile or
policy is missing, or when the invocation gate or a durable manager was replaced after
the assistant. `AddNewHeapAssistant` is idempotent. The assistant relies on the
registered `INhAiAuthenticatedInvocationContextResolver`; a replaced resolver is used
as is, and no tenant claim is required.

`INhAssistantToolPresenter` is optional. Use it for application-localized names and
approval explanations sourced from authorized server-side data. Presentation is
persisted beside, but never substituted for, the exact proposal evidence. The API
returns it as optional `approval.presentation`; clients must support `null` for old
approvals and tools without a presenter.

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
| `GET conversations/{id}` | conversation with messages and pending approval; for an agent thread also `kind: "thread"` and `thread` with the subject, the caller's role and who may decide each open proposal |
| `DELETE conversations/{id}` | `204`; the conversation is archived and hidden |
| `POST conversations/{id}/messages` `{ text, clientMessageId, clientContext? }` | `text/event-stream`; `409` when the conversation is not idle |
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

Agent threads and runs (see `ConfigureAgent`, `AddSubjectType` and `AddTrigger`) add
these endpoints. Access follows the caller's role on the subject, which the subject's
`INhAiSubjectAccessResolver` decides on every request; people without a role receive
`404`.

| Method and path | Response |
|---|---|
| `GET threads?agentId=&subjectType=&subjectId=` | the agent's thread about the subject, the caller's role and whether the caller may start the agent |
| `POST threads/start` `{ agentId, subjectType, subjectId, message? }` | `202` trigger receipt; needs the participant role |
| `GET threads/{id}/runs` | runs, newest first, with status, failure class, usage and proposal count |
| `GET threads/{id}/consultations?page=&itemsPerPage=` | who consulted the agent through which assistant, newest first, with outcome, usage and proposal count; never the question or the answer |
| `GET threads/{id}/proposals` | proposals with their decision and execution state and the application's `presentation` |
| `GET threads/{id}/memory` | the thread's working memory, its schema version and last change |
| `GET threads/{id}/artifacts` | the files the agent's tools saved, with name, version, size and `restricted` |
| `GET threads/{id}/artifacts/{artifactId}/content` | the file's content; `404` when it is restricted for the caller |
| `POST runs/{runId}/cancel` | `202`; needs the participant role |
| `POST runs/{runId}/retry` `{ mode: resume or restart }` | `202` trigger receipt; needs the approver role |
| `GET approvals/inbox?subjectType=&agentId=&divisionId=&runId=&page=&itemsPerPage=` | pending proposals the caller may decide, by expiry |
| `POST approvals/{id}/decisions` `{ decision, expectedProposalHash?, reason? }` | the decided proposal; `403` with `assistant-four-eyes-required` or `assistant-approval-policy-denied` |
| `POST approvals/decisions` `{ approvalIds, decision, reason? }` | one outcome per proposal |
| `POST approvals/{id}/resolve` `{ action: re-execute or mark-resolved, reason? }` | the proposal after a person resolved an execution with an unknown outcome |
| `PUT approvals/{id}/feedback` `{ category, text? }` | the caller's feedback on a rejected, expired, withdrawn or stale proposal; `403` for people who neither decided it nor approve on the subject, `409` with `assistant-feedback-not-accepted` for a pending or approved proposal |
| `POST approvals/feedback` `{ approvalIds, category, text? }` | one outcome per proposal; the records are marked as bulk feedback |

`POST triggers/{triggerId}` fires a webhook trigger without a user. Sign
`{timestamp}.{deliveryId}.{body}` with HMAC-SHA256 and the trigger's secret and send
`X-NH-Timestamp` (Unix seconds), `X-NH-Delivery` and `X-NH-Signature` (`sha256=` plus the
lowercase hex digest). It returns `202` with a receipt, `401` with
`assistant-webhook-signature-invalid` for a missing, stale or wrong signature, `400` when
the body names no subject and `413` above 64 KB. Administrators stop or resume an agent
with `POST admin/agents/{id}/kill-switch` `{ engaged }`.

Administrators review why people reject an agent's proposals. For proposals made in a
period (`from`, `to`; the last 30 days by default, at most 366 days),
`GET admin/agents/{id}/feedback/summary` counts approved, rejected, expired and withdrawn
proposals per tool and agent version, with the rejection rate, the feedback categories and
the number of different people who gave feedback. `GET admin/agents/{id}/feedback` lists
the feedback with the proposal's presentation, filtered by `scopeKey`, `toolId`,
`category` and `agentVersion`, and `GET admin/agents/{id}/feedback/export` streams it as
JSON Lines with each proposal's tool input (`limit`, at most 50,000). The export holds
subject data and is audited as `AdminAgentFeedbackExported`.

The generated consultation tools of every `AllowConsultation` registration are an attested
tool catalog of this package. They run through the shared invoker with the asking person's
rights and return the shared `NhAiToolResult` shape to the model, `{ success, data }` or
`{ success: false, code, message }`, with codes such as `assistant-consult-subject-unavailable`. The startup validator checks
that each consulted subject type has a resolver and each consultation policy exists. An
`INhAiToolDiscoveryPolicy` that allows tool prefixes must allow `assistant.consult.` and
`assistant.request.`.

A message to a thread while a run is active returns `202` with a receipt; the next run
receives it. Turns that a person types in a thread are bound to the request like chat
turns and their approvals are inline.

`GET conversations/{id}` of a thread returns `kind: "thread"` and `thread`
(`NhAssistantConversationThreadDto`): the subject, the caller's live role and, per open
proposal (`NhAssistantThreadDecisionDto`), its `runId`, `canDecide` and a `reasonCode`
such as `assistant-four-eyes-required`, checked like the decision itself. Clients decide a
proposal with a run, and a deferred proposal of a consultation (`consultationId`), through
`POST approvals/{id}/decisions`; an inline proposal without a run belongs to a person's
turn and resumes it through `POST conversations/{id}/approvals/{approvalId}/decide`.
User messages carry `authorKind`: `person`, or `trigger` for text a trigger wrote into the
thread.

The live hub requires the assistant access policy like the endpoints. Browsers send no
custom headers with WebSockets and server-sent events, so a policy that needs the active
division reads it from the `divisionId` query value: the NewHeap JWT handler copies it to
`X-NH-ActiveDivisionId` on `/hub` paths. The Angular client sends it through
`NhAssistantConfig.getHubQueryParameters`.

For agent threads the hub also sends `AgentWorkChanged` (`NhAssistantLiveAgentWorkDto`):
a run, a proposal or its feedback changed. It goes to the thread's participants and to
everyone in the subject's member snapshot, carries only identifiers and a reason (`run`,
`proposal` or `feedback`), and lets clients reload the agent-run endpoints they show.
Proposals are presented in every supported UI culture of `RequestLocalizationOptions`;
each endpoint returns the presentation of the request's culture.

Every error of `preferences` and `admin/*` has the body `{ code, messageKey, errors? }`.
Validation errors return `400` with code `assistant-validation` and `errors` keyed by
camelCase field name (values `required`, `invalid`, `too-long`, `not-found`); blocked
hosts return `400` with `assistant-mcp-host-blocked`. A caller without the admin policy
receives `403` with `assistant-forbidden`, unknown objects `404` with a specific
`*-not-found` code and stale versions `409`. MCP secrets are protected with ASP.NET Data Protection; persist the key
ring across restarts and nodes.

`clientContext` is `{ route, title?, entities?: [{ type, id, label? }] }` (route 200,
title 120, five entities, dash-case type, id 64, label 120 characters). The server cuts
off long text and ignores a value of the wrong shape instead of answering `400`.

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
