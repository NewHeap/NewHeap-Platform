# v-next

## NewHeap.Platform.Media.FileStructureStorage.SqlServer: configured media schema

SQL Server media migrations now apply to the schema configured through
`FileStructureDbContextOptions.Scheme`, whether the startup migration, a direct
`MigrateAsync` or a generated migration script runs them. As the PostgreSQL
provider already did, the provider routes every migration operation to that
schema while it generates SQL, and keeps a separate EF model per schema. A
direct `MigrateAsync` with a custom schema no longer reports pending model
changes.

This fixes the `20260902133212_IndexSeekLookup` upgrade, which always targeted
`nhmedia`. With any other schema, the startup migration failed and was rolled
back while the package already used the new `PathLookup` columns, so media
requests failed with `Invalid column name 'PathLookup'`. Applications on the
default `nhmedia` schema and PostgreSQL applications were not affected; their
migration SQL is unchanged.

| Adoption note | Required action |
|---|---|
| A SQL Server media database with a custom `Scheme` still has `20260902133212_IndexSeekLookup` pending after the failed upgrade. | Update the package and restart the application; the startup migration, or your own `MigrateAsync` call when `RunMigrations` is disabled, applies the pending migration in the configured schema. |
| A database where the same migration was applied by hand in the configured schema and recorded in that schema's `_migrations` table is already up to date. | No action; the migration is skipped. |
| `BaseMigration.DefaultScheme` now always returns the schema the migrations are authored in, `nhmedia`, instead of the configured schema. | Read `FileStructureDbContextOptions.Scheme` when you need the configured schema. |

## NewHeap.Platform.AI.Chat: agents that run on their own

An assistant agent can now start without a person in the conversation: from an
application event (`AddTrigger<TEvent>` and `INhAssistantAgentTriggers`) or when a
person starts it on a subject (`POST threads/start`). Each run is a background
operation of its own, so it survives restarts, retries transient model failures and
honours cancellation. `ConfigureAgent` describes who answers for the agent's runs
(the agent itself by default, or the subject's responsible person or a service
account), which tools the agent may use beyond the rights of the person it works
for, its daily budget, retries, kill switch and shadow mode.

The agent keeps one thread per subject, such as a project. Nobody owns a thread:
an `INhAiSubjectAccessResolver` registered with `AddSubjectType` decides on every
request who reads it, types in it and decides its proposals. Agent actions that
change state become proposals under a named approval policy (`AddApprovalPolicy`):
inline proposals pause the run until the decision, deferred proposals let the run
end and wait in the thread and in the approval inbox (`GET approvals/inbox`).
Policies name the approvers, four eyes, the lifetime and expiry action, reminders
and whether an edited subject makes an approved proposal stale. Approved proposals
execute as their own background operation; a non-idempotent tool runs at most once,
and an execution with an unknown outcome waits for a person (`POST approvals/{id}/resolve`).

Agents also start on a schedule (`AddScheduledTrigger`, a Hangfire recurring job that
fires for each subject a query returns) and from external systems
(`AddWebhookTrigger` and `POST {prefix}/triggers/{id}`, signed with HMAC-SHA256 over
timestamp, delivery id and body). A burst of events on one subject joins one pending run.
Administrators stop an agent with its kill switch (`POST admin/agents/{id}/kill-switch`),
which cancels its active runs and holds its approved proposals until it is released.
The subject's approvers are notified of waiting proposals, due reminders, escalations,
unknown outcomes and failed runs through Web Push and the channels registered with
`AddAgentNotificationChannel`; `UseAgentNotificationPolicy` changes the recipients.
Shadow agents notify nobody and their threads are visible to administrators only.

Agents remember between runs. `Memory<TMemory>(schemaVersion)` gives a thread a typed
working memory the model reads and changes with JSON Merge Patch through the internal
`memory_get` and `memory_update` functions; a patch that does not fit `TMemory`, exceeds
16 KB or lost a race changes nothing, and `UseAgentMemoryMigrator` upgrades older schema
versions. Tools save larger output as versioned files of the thread with `INhAiArtifacts`
(at most `NhAssistantLimits.MaxArtifactBytes`). Every run ends with a bounded summary, and
the next run starts with the memory, the file names and versions and the last
`MaxRunSummaries` summaries. `GET threads/{id}/memory`, `GET threads/{id}/artifacts` and
`GET threads/{id}/artifacts/{artifactId}/content` show them to the people on the subject.
Results that the agent obtained with its own grant are shown only to people who satisfy
the tool's policy; tool parts carry `grantSource` and `restricted`. After the
application deleted a subject, `INhAiSubjectRetention.ForgetSubjectAsync` cancels its
open runs and deletes its threads with their memory, summaries and files.

Proposals in `GET threads/{id}/proposals` and the approval inbox carry the
application's `presentation`, the same localized explanation the chat approval card
shows, so approvers read what changes instead of the tool input. The assistant
presents each proposal in every supported UI culture of `RequestLocalizationOptions`
when it is made, and each reader gets their own request culture's presentation, also
for proposals of headless runs, which run in the default request culture. When a
deferred proposal closes without running, its tool call in the thread reads `rejected`
or the new status `not-executed` instead of waiting. The live hub's new
`AgentWorkChanged` message (`NhAssistantLiveAgentWorkDto`) tells the thread's
participants and everyone on the subject that a run, a proposal or its feedback changed,
so inboxes and thread views refresh without polling. Proposals carry
`DecidedByDisplayName` from the subject's member snapshot.

`GET conversations/{id}` of a thread says who decides, so a chat client does not apply
the chat rule that only a turn's author decides: it returns `kind: "thread"` and
`thread` (`NhAssistantConversationThreadDto`) with the subject, the caller's live role
and, per open proposal (`NhAssistantThreadDecisionDto`), its run, `canDecide` and a
`reasonCode` such as `assistant-four-eyes-required`, from the same check as the
decision. User messages carry `authorKind` (`NhAssistantMessageAuthorKinds`): `person`,
or `trigger` for text a trigger wrote into the thread.

Tools opt in to agent grants with `NhAiToolAttribute.AllowAgentGrant`, which
requires agent exposure and an `[Authorize]` policy (`NHAI014`). An agent that
answers for itself never executes a non-read effect without approval. Model calls
now retry transient provider failures twice with backoff before a turn fails; a
streamed answer is retried only before its first update.

| Adoption note | Required action |
|---|---|
| The `nhai` schema gains the `AgentRuns` migration: threads, runs, queued inputs, subject members, and run, policy and execution columns on approvals and tool invocations. | Update the package and restart; the assistant applies the migration on startup when `RunMigrations` is on. Otherwise apply it with your own migration step for SQL Server or PostgreSQL. Existing conversations become conversations of kind `chat` and keep working. |
| Agents with `ConfigureAgent` need background operations. | Call `operations.AddAssistantAgentRuns()` inside `WithBackgroundOperations`; the host fails at startup without it. Hosts without `ConfigureAgent` need nothing. |
| `INhAiBackgroundOperationRunAdapter` has a new member `BindProposalExecution`. | Implement it when you replaced the adapter; the platform adapter already does. |
| Setting `AllowAgentGrant` adds the agent grant to a tool's contract hash. | Pending approvals for that tool expire when you opt in; let people approve again. |
| Bridge and MCP tools forward a caller's own credentials and are not offered in headless runs. | Give agents curated generated tools for work they must do on their own. |
| Scheduled triggers register Hangfire recurring jobs at startup. | Configure `WithHangfire` in hosts that use `AddScheduledTrigger`; the host fails at startup without it. |
| Webhook triggers are anonymous endpoints authenticated by their signature. | Keep each trigger's secret at least 32 characters, store it outside source control and rotate it through configuration. |
| `NhAssistantAuditEventKind` gains agent trigger, run, execution and kill-switch kinds. | Let business audit sinks accept the new kinds; they carry identifiers and codes only. |
| The `AgentRuns` migration also adds the `AssistantArtifact` table and approval reminders (`NextReminderAt`). | Nothing beyond applying the migration. |
| Agent presenters and other texts of headless runs use `RequestLocalizationOptions.DefaultRequestCulture`; approval presentations are also made in every supported UI culture. | Make your `INhAssistantToolPresenter` answer in every supported UI culture; the model's own text stays in the default request culture. |
| The `AgentProposalPresentations` migration adds `AssistantApproval.PresentationsJson`. | Apply it with the other `nhai` migrations; proposals made before keep their one presentation. |
| `INhAssistantHubClient` gains `AgentWorkChanged`, and tool calls gain the status `not-executed`. | Handle unknown hub messages and tool statuses in custom clients. |
| An assistant request whose `Accept-Language` names only a language, such as `en` from the Angular client, now uses the supported culture of that language when request localization matched no culture; it used the default culture before. | Nothing; configure `SupportedUICultures` with the cultures your users read. |
| Deleting a subject leaves its agent threads behind unless the application tells the assistant. | Call `INhAiSubjectRetention.ForgetSubjectAsync` after the delete commits. |
| `NhAssistantConversationDto` gains the optional `Kind` and `Thread` parameters and `NhAssistantMessageDto` the optional `AuthorKind`. | Recompile code that constructs these records; JSON clients ignore or read the new fields. |

## NewHeap.Platform.AI.Chat: consulting another agent

An assistant can now consult another agent about a subject, for example a general
assistant that asks a work-preparation agent what is still missing for a project.
`AllowConsultation(subjectType, ...)` in `ConfigureAgent` generates the governed tool
`assistant.consult.{agentId}.{subjectType}` and, with `AllowRequests()`,
`assistant.request.{agentId}.{subjectType}`; an asking agent uses them when its tool
selectors select them. A consult answers in the same turn: the agent reads its working
memory, run summaries and recent thread with the asking person's rights only, never with
its own grants, writes no message into its thread and turns every change into a deferred
proposal for the subject's approvers (`NhAssistantProposalDto.ConsultationId`), which
executes with the asker's rights. A request starts an ordinary run with the asker as
initiator and notifies them when it ends (`NhAssistantAgentNotificationKind.RequestAnswered`).
`GET threads/{id}/consultations` lists who consulted the agent, without question or
answer; `NhAssistantAuditEventKind.AgentConsulted` and `AgentRequested` audit them.
`NhAssistantLimits.MaxConsultationDepth` (1) and `MaxConsultationsPerActorPerDay` (50)
bound consultations, and they spend the consulted agent's daily budget.
`NhAiAgentInvocationScope.RequireApprovalForEffects` makes the tool invoker require an
approval for every effect other than a read in such a scope, whatever the effect policy
decides.

| Adoption note | Required action |
|---|---|
| The `nhai` schema gains the `AgentConsultations` migration: the `AssistantConsultation` table and `AssistantApproval.ConsultationId`. | Update the package and restart; the assistant applies the migration on startup when `RunMigrations` is on. Otherwise apply it with your own migration step for SQL Server or PostgreSQL. |
| An `INhAiToolDiscoveryPolicy` that allows only known tool prefixes hides the generated consultation tools. | Allow `assistant.consult.` and `assistant.request.` where agents may consult each other. |
| The asking model types the subject id of a consultation. | Let `INhAiSubjectAccessResolver` accept only a subject's canonical id, so another spelling never opens a second thread. |
| `NhAssistantAuditEventKind` gains `AgentConsulted` and `AgentRequested`, and `NhAssistantAuditEvent` gains consultation and usage fields. | Let business audit sinks accept the new kinds and fields; they carry identifiers, codes and counts only. |

## NewHeap.Platform.AI.Chat: feedback on rejected agent proposals

After a person rejected an agent proposal, they may say what was wrong with
`PUT approvals/{approvalId}/feedback`: a category from
`NhAssistantProposalFeedbackCategories` (`incorrect`, `wrong-value`, `not-needed`,
`not-now`, `already-done`, `not-allowed` or `other`) and an optional text of at most
1,000 characters. The person who decided the proposal and the subject's approvers may
give feedback on rejected, expired, withdrawn and stale proposals, one record each that
they may change; `POST approvals/feedback` stores the same feedback for proposals
rejected together. `NhAssistantProposalDto` carries the caller's own `Feedback` and
`CanGiveFeedback`. The next run on the same subject starts from the thread's recent
rejections and their feedback (`NhAssistantLimits.MaxRejectionsInContext` and
`RejectionContextWindow`), so it does not propose them again unchanged; consultations
do not receive them. Administrators read rejection rates and categories per tool and
agent version with `GET admin/agents/{id}/feedback/summary`, the feedback with
`GET admin/agents/{id}/feedback` and an audited JSON Lines export with
`GET admin/agents/{id}/feedback/export`. `AddProposalFeedbackSink` forwards every
feedback, with its text, to the application's analytics; the business audit sink
receives `AgentProposalFeedbackGiven` with the category only. Feedback is deleted with
the subject's threads.

| Adoption note | Required action |
|---|---|
| The `nhai` schema gains the `AgentProposalFeedback` migration: the `AssistantProposalFeedback` table. | Update the package and restart; the assistant applies the migration on startup when `RunMigrations` is on. Otherwise apply it with your own migration step for SQL Server or PostgreSQL. |
| `NhAssistantAuditEventKind` gains `AgentProposalFeedbackGiven` and `AdminAgentFeedbackExported`. | Let business audit sinks accept the new kinds; they carry identifiers and the category only. |
| A new agent run's continuity block lists the thread's rejected proposals of the last 30 days, at most five. | Set `MaxRejectionsInContext` to `0` to leave them out, or change `RejectionContextWindow`. |
| Feedback texts are people's words about a subject. | Cover them in your processing agreements, and keep them out of logs in your `INhAssistantProposalFeedbackSink`. |

## NewHeap.Platform.AI.Chat: agent work on the application's own subjects

Applications show agent work where people look, such as on a project, instead of only in the
approval inbox. `GET subjects/{type}/{id}/work` returns the caller's role, every agent with a
thread on the subject (and the requested `agentId`) and the open proposals of all of them with
the caller's live decision right (`NhAssistantSubjectWorkDto`).
`POST subjects/{type}/summary` counts open proposals, proposals the caller may decide and
active runs for up to 200 subjects in one query, for badges in lists
(`NhAssistantSubjectSummaryListDto`). `POST threads/open` returns the agent's thread about a
subject and creates it without starting a run, so a page can open the conversation with the
agent about its subject. `INhAssistantSubjectWork` offers the same reads to the application's
own back-end with the same access checks (`GetForRequestAsync`, `SummarizeForRequestAsync`
for the current request), and `AgentWorkChanged` now names the subject.

| Adoption note | Required action |
|---|---|
| `NhAssistantLiveAgentWorkDto` gains `SubjectType` and `SubjectId`. | Recompile code that constructs it; clients may refresh by subject now. |

## @newheap/platform-ai-chat: agent work in the application

The new entry point `@newheap/platform-ai-chat/agent-runs` shows the work of agents
that run on their own: `nh-assistant-agent-inbox` lists the proposals waiting for the
signed-in person, `nh-assistant-agent-thread-view` shows one agent's thread on one
subject with its proposals, runs, working memory and files and lets participants start
the agent, and the proposals, run timeline, memory and files are also separate
components. Proposals show the application's presentation and keep the tool input
under collapsed technical details. `NhAssistantAgentRunApiService` calls the agent-run
endpoints. The tool-call card marks results of agent grants and shows restricted
results without content, and the approval card names the execution states of agent
proposals. The thread view has a Consultations tab (`nh-assistant-agent-consultations`)
that shows who consulted the agent through which assistant, and a proposal from a
consultation carries a badge; `NhAssistantAgentRunApiService.getConsultations` reads them.

The panel opens agent threads with the right decision rules. The subject's approvers
decide the agent's proposals, not the author of a turn, and several proposals can wait
at once: `NhAssistantStore.approvalRights` gives each approval card its own right from
the conversation's `thread.decisions`, and `NhAssistantStore.decide(decision, reason,
approvalId)` decides a run's proposal with its proposal hash through
`POST approvals/{id}/decisions` and reloads the thread. A card the caller may not decide
says who does through its new `waitingFor` input: an approver, or another approver when
four-eyes keeps out the person who started the work. Messages a trigger wrote read as an
event instead of a participant. `nh-assistant-thread` takes `approvalRights` and emits
`approvalChoice` with the approval id.

`NhAssistantConfig.getRequestHeaders` adds host headers to every assistant request and
to the live-update connection, for example the NewHeap active division
(`X-NH-ActiveDivisionId`) that active-division access policies and tools need. The
library keeps its own `Authorization`, `Accept`, `Accept-Language` and `Content-Type`.
Browsers send no custom headers with WebSockets and server-sent events, so
`NhAssistantConfig.getHubQueryParameters` adds query parameters to the live hub URL, for
example `{ divisionId }`, which the NewHeap JWT handler copies to `X-NH-ActiveDivisionId`
on `/hub` paths.

| Adoption note | Required action |
|---|---|
| The assistant's requests did not carry the active division, so an access policy that requires it refused them. | Pass `getRequestHeaders` with the active division when the API's access policy or tools are active-division policies. |
| Browsers send no custom headers with WebSocket and server-sent event connections. | Pass the values a hub's policy needs with `getHubQueryParameters`, for the active division `() => ({ divisionId })`; without it the connection falls back to long polling after refused attempts. |
| A custom layout with `nh-assistant-thread` showed every proposal of an agent thread as decidable by the turn's author. | Pass `[approvalRights]="store.approvalRights()"` and handle `(approvalChoice)` with `store.decide(choice.decision, reason, choice.approvalId)`; `approvalDecision` alone decides only the conversation's pending approval. |
| `ApprovalStatus` gains the execution states of agent proposals. | Handle unknown statuses in custom approval layouts. |

## @newheap/platform-ai-chat: feedback on rejected agent proposals

Rejecting an agent proposal stays one click; afterwards
`nh-assistant-proposal-feedback` (`NhAssistantProposalFeedbackComponent`) asks, without
blocking, what was wrong: a category from `NH_ASSISTANT_FEEDBACK_CATEGORIES` and an
optional text, sent with `NhAssistantAgentRunApiService.giveFeedback` or, for proposals
rejected together, `giveFeedbackMany`. `nh-assistant-agent-proposals` shows the question
after a rejection, also in the inbox once the rejected proposal left it, offers "Give
feedback" or "Change feedback" on closed proposals with `canGiveFeedback`, and shows the
caller's own `feedback`. The panel's approval card asks the same question for proposals
the user rejected in this session (`NhAssistantApprovalRight.canGiveFeedback`, the card's
`canGiveFeedback` input). Administrators open `nh-assistant-agent-feedback-report`
(`NhAssistantAgentFeedbackReportComponent` of `@newheap/platform-ai-chat/admin`) from an
agent in the administration page, or place it in their own page: rejection rates and
categories per tool and agent version, the feedback texts and a JSON Lines export through
`NhAssistantAdminApiService.getAgentFeedbackSummary`, `getAgentFeedback` and
`exportAgentFeedback`.

| Adoption note | Required action |
|---|---|
| `nh-assistant-agent-proposals` no longer shows a shared reason field before approve and reject; the key `nh-assistant.agent-runs.proposal.reason` is gone. | Remove overrides of that key; collect why a proposal was rejected through the feedback question instead. |
| A custom layout with `nh-assistant-thread` offers feedback only when it passes the store's `approvalRights`. | Keep `[approvalRights]="store.approvalRights()"`; the rights now also carry `canGiveFeedback`. |

The agent-run components and the panel now follow agent work live:
`NhAssistantLiveService.agentWork$` emits the hub's `AgentWorkChanged`
(`LiveAgentWorkChanged`), the inbox and the thread view with its tabs reload after a
short pause, and the store reloads an open agent thread, so a decision made in the
panel, the inbox or by a colleague shows everywhere without Refresh. Tool calls of
proposals that closed without running show the new status `not-executed` ("Not run"),
and the tool group stops saying "Waiting for approval" once no call waits. Proposals show
who decided by `decidedByDisplayName`, and the feedback report its `authorDisplayName`,
with the id as fallback.

| Adoption note | Required action |
|---|---|
| `ToolCallStatus` and `NhAssistantToolCallGroupState` gain `'not-executed'`. | Extend exhaustive switches or `Record` types over them. |
| `NhAssistantLiveService` gains `agentWork$`. | Add it to custom stand-ins of the live service. |

Agent work also appears on the host's own pages. `nh-assistant-subject-badge` shows, per
subject, the proposals the viewer may decide or the open ones and whether an agent works on
it; badges on one page share one summary request through `NhAssistantSubjectSummaryService`.
`nh-assistant-subject-suggestions` of `@newheap/platform-ai-chat/agent-runs` shows the open
proposals of every agent on one subject with the viewer's decision rights and opens the
conversation through `NhAssistantPanelService.openSubject(agentId, subject)`, which creates the
agent's thread without a run. `NhAssistantAgentRunApiService` gains `getSubjectWork`,
`summarizeSubjects` and `openThread`, and `nh-assistant-agent-proposals` a `decisionRights`
input that overrides `canDecide` per proposal.

| Adoption note | Required action |
|---|---|
| `LiveAgentWorkChanged` gains `subjectType` and `subjectId`. | Add them to test stand-ins that build the event. |
| `NhAssistantPanelService` uses `NhAssistantAgentRunApiService`. | Nothing with `provideNhAssistant`; provide the service yourself in custom setups without it. |
| `NhAssistantAgentProposalsComponent.pending` lists the pending proposals the caller may decide. | Read `proposals()` for every pending proposal. |

## NewHeap.Platform.AspNet.Common: operations owned by agents, and access for others

A background operation now has an owner kind. Besides users, an agent or system
key can own an operation (`NhBackgroundOperationEnqueueOptions.OwnerAgentKey`); such
operations send no live updates or notifications to a user and never appear in a
user's own list. `operations.AddAccess<TAccess>()` registers an
`INhBackgroundOperationAccess` policy that lets other people view, cancel, retry or
signal an operation, for example the members of the subject an agent works on.
`INhBackgroundOperationSignalService.SignalAsync` signals on behalf of such a person.
`NhBackgroundOperationEnqueueOptions.NotBefore` delays the first dispatch. An enqueue
that joins the caller's transaction now publishes its live update after that
transaction commits instead of before.

The active-division authorization handler now accepts a resource that implements
`INhDivisionScopedResource`: the resource's division is checked instead of the
request header. Without an HTTP request and without such a resource the requirement
no longer succeeds, so background work cannot pass an active-division policy by
accident.

| Adoption note | Required action |
|---|---|
| `NhBackgroundOperation.OwnerUserId` is nullable and the table gains `OwnerKind`, `OwnerKey` and indexes on owner, domain object and concurrency key. | Generate a consumer DbContext migration with the library upgrade and deploy it before the new version runs. Existing rows become user-owned; no data changes. |
| Code that reads `OwnerUserId` from operations, summaries or views. | Handle `null` for agent-owned operations, or read `OwnerKind`. |
| Code that evaluated an active-division policy outside a request, for example in a job. | Pass an `INhDivisionScopedResource` with the division to `IAuthorizationService.AuthorizeAsync`, or authorize in the request that starts the job. |

## @newheap/platform-common: sessions the server rejects

A browser session now ends when the API no longer accepts it, for example after
the password was changed on another device or the token was revoked. Until now
the browser kept a session that looked valid, so every request and every
notification poll answered 401 until the user logged out by hand.

`NhSessionRejectionInterceptor`, registered by `NhCommonModule`, reacts to a 401
from `apiBaseUrl` or `authApiBaseUrl` while a session exists. It calls the new
`BaseNhAuthService.verifyServerSession()`, which asks the account information
endpoint once for all concurrent failures. Only a 401 from that endpoint ends
the session; an endpoint that answers 401 for another reason never signs the
user out. The session then ends through `clearAuthorization()`, and
`AuthSessionExpirationInformation.rejectedByServer` tells the application why.
`NhUserNotificationService` no longer polls without a session and no longer logs
a 401 as a notification failure.

| Behavior change | Required action |
|---|---|
| A 401 that the account endpoint confirms clears the stored authorization and emits `authSubject` and `sessionExpirationInformationChanged`. | Nothing when the application already routes to login after the session ends; otherwise do that on `sessionExpirationInformationChanged`, and use `rejectedByServer` to explain it. To keep the old behavior, set `authentication.endSessionRejectedByServer` to `false`. |
| A request that may answer 401 without meaning the session ended can skip the check. | Set `NH_SKIP_SERVER_SESSION_CHECK` in the request's `HttpContext`. |

## NewHeap.Platform.AI.Common: tool results the model can act on

Every governed tool now gives the model or protocol caller one result shape,
`NhAiToolResult`: `{ "success": true, "data": ... }` on success and
`{ "success": false, "code": "...", "message": "..." }` on failure, with `data` when
the failed `TaskResult<T>` carries a typed payload. Before, the model received the
serialized `TaskResult<T>`, whose `allErrorMessages` carried neither the result code
nor the message text, so it could not tell one failure from another. The code is
the first result item named with a lowercase dash-case code, otherwise
`ai-tool-failed`. `NhAiGovernedAIFunction.ReturnJsonSchema` is the shape around the
descriptor's output schema. Contract hashes, manifests and attestation are unchanged.

| Breaking change | Required action |
|---|---|
| Governed functions return `NhAiToolResult` instead of the serialized `TaskResult<T>`; `allErrorMessages` is gone. | Read `success`, `code`, `message` and `data`; name expected failures with a dash-case code, such as `TaskResult<T>.Failed("project-not-found", "...")`. |
| A failure without a coded item reports every message with its item name, in the tool result and in the MCP `newheap.com/message` metadata. | Match on the code, never on the message text. |
| `NhAiGovernedAIFunction` publishes the shared shape as its return schema, whatever its inner function returns. | Create the functions of a hand-written attested catalog with `MarshalResult = NhAiToolResult.CreateMarshaller<T>(options)` and `ExcludeResultSchema = true`. |

## NewHeap.Platform.AI.Generators: tool results the model can act on

| Breaking change | Required action |
|---|---|
| Generated functions return the shared `NhAiToolResult` shape; its property names stay lowercase under a declared context's naming policy, and only `data` follows the context. | Read `success` and `data` in lowercase; a `JsonSerializerContext` now needs only the tool input and output types and may drop `TaskResult<TOutput>`. |

## NewHeap.Platform.AI.AspNet.Mvc: tool results the model can act on

| Breaking change | Required action |
|---|---|
| Bridge and gateway tools return `NhAiBridgeResponse` as `data` of the shared shape, and a failure carries its `api-bridge-*` or pipeline `code` and `message`. | Read the bridge failure from `code`; `api-bridge-validation` still keeps the response with the model state as `data`. |

## NewHeap.Platform.AI.Mcp: tool results the model can act on

| Breaking change | Required action |
|---|---|
| Enveloped exports publish the shared shape as structured content and output schema; a failure with typed data carries `code` and `message` instead of `allErrorMessages`. | Read the code from structured content, `_meta` or `NhAiMcpToolException.Code`; `CallNewHeapToolAsync` and flat exports are unchanged. |
| Imported MCP functions return the shared shape with the remote `CallToolResult` as `data` instead of a `TaskResult<CallToolResult>` object, and a remote `isError` result fails with `ai-mcp-remote-tool-error` in the result and the audit record instead of without a code. | Read the remote result from `data`; match remote failures on `NhAiMcpImportedToolCodes.RemoteError`. Verifiers still receive the typed `CallToolResult`. |
