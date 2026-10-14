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
