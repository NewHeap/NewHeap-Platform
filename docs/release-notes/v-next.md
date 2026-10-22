# v-next

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: trigger event filters per data trigger

An agent no longer needs to start itself again with a change it made. A trigger event registers
named filters with `NhAssistantTriggerEventBuilder.Filter(name, description, (evt, ctx) => ...)`,
and a data trigger selects the ones it applies (`Filters` of `NhAssistantDataTriggerRequest` and
`NhAssistantDataTriggerDto`, at most `NhAssistantAgentConfigurationLimits.MaxTriggerFilters`). The
run catalog lists each event's filters with their descriptions (`NhAssistantRunBlockDto.Filters`).
Every selected filter must accept an event; the event's `When` decides first, then its `Subject`
function runs, then the filters, and only then the message, dedupe and payload functions. A filter
receives `NhAssistantTriggerFilterContext` with the agent, skill, trigger, the subject the trigger
fires for and `AgentActorId`, which equals `NhAiInvocationContext.ActorId` in the agent's tool calls
and `NhAiAgentInvocationScopes.Current.AgentActorId` there. A refusing filter ignores the event with
`filtered`, without an audit. Saving a filter the event does not register fails with
`assistant-trigger-filter-not-found` (`400`, field error `filters: not-found`). A stored filter that
a deploy removed fails closed: the trigger ignores every event with the new reason
`NhAssistantTriggerReasons.FilterMissing` (`filter-missing`) and is reported with the new problem
`NhAssistantRunConfigurationProblems.UnknownFilter` (`unknown-filter`).

| Breaking change | Required action |
|---|---|
| `AssistantAgentTriggerDefinition` gained the nullable column `FiltersJson` (migration `TriggerEventFilters` for SQL Server and PostgreSQL). | Apply the assistant migrations before deploying. |
| `NhAssistantDataTriggerRequest` gained the optional last parameter `Filters`; the constructor's signature changed. | Recompile code that constructs the request; source that names its arguments compiles unchanged. |
| For a data trigger with filters, the event's subject function now runs before the filters, and the fire reuses its result. | Keep `Subject` free of side effects, as `When` already is. |

## @newheap/platform-ai-chat: trigger event filters per data trigger

The administration's data trigger form shows the chosen event's filters with their descriptions as
check boxes, says when the event has none, and shows a stored filter the event no longer offers as
missing, which blocks saving until it is cleared; the trigger list shows the applied filters.
`RunBlock.filters`, `DataTrigger.filters` and `DataTriggerRequest.filters` carry them,
`assistant-trigger-filter-not-found` is in `NH_ASSISTANT_SERVER_ERROR_CODES`, and the mock admin API
validates and stores filters as the server does. English and Dutch texts are included.

| Breaking change | Required action |
|---|---|
| None; the new fields are optional and missing on older servers. | None. |

## NewHeap.Platform.AI.Common, NewHeap.Platform.AI.Chat, NewHeap.Platform.AI.Chat.SqlServer, NewHeap.Platform.AI.Chat.PostgreSql and NewHeap.Platform.AI.Chat.AspNet: the approval inbox overview

A subject access resolver may name the subject a subject belongs to, such as the project of a task
(`NhAiSubjectInfo.Parent` with `NhAiSubjectParent`). Every proposal records that parent's type, id and
name when it is made, and `NhAssistantProposalDto.Parent` (`NhAssistantProposalParentDto`) carries it.
`GET approvals/inbox?grouped=true` groups the proposals by parent, or by their own subject without one,
contiguous across pages, and `NhAssistantInboxDto.Groups` tells per group and subject on the page its
count, its total and whether it continues before or after the page. The inbox marks what is new since
the caller's last visit in its scope (`ScopeKey`, `LastVisitedAt`, `NewApprovalIds`), which
`POST approvals/inbox/visit` records (`NhAssistantInboxVisitDto`). Without `grouped` the inbox keeps its
order. A resolver that names a subject as its own parent is refused.

| Breaking change | Required action |
|---|---|
| `AssistantApproval` gained the nullable columns `ParentSubjectType`, `ParentSubjectId` and `ParentDisplayName` (migration `ProposalParents` for SQL Server and PostgreSQL). | Apply the assistant migrations before deploying. |
| `AddAgentGroup` refuses group ids that start with `nh-`; the platform keeps the inbox's visits under `nh-inbox`. | Rename such a group and move its memberships in the administration. |

## @newheap/platform-ai-chat: the approval inbox overview

`nh-assistant-agent-inbox` takes `divisionId`, `runId`, `grouped`, `groupByRun`, `markNew` and
`recordVisit`; changing a filter or the grouping reloads from the first page. Grouped, it shows a header
per parent and subject, optionally per run, how many of a group it shows and whether the group
continues, and "Select group" selects only the shown pending proposals the person may decide. It marks
proposals made since the last visit and those that arrive while it is shown, and records the visit
once (`NhAssistantAgentRunApiService.visitInbox`). `nh-assistant-agent-proposals` takes `groups`,
`groupByRun` and `newIds`. `nh-assistant-run-result` reads the reader's live decision rights and lets
them decide the run's proposals one by one or together. New models: `AgentInboxGroup`,
`AgentInboxSubjectGroup`, `AgentInboxVisit`, `AgentProposalParent`, and the optional `AgentInbox` and
`AgentInboxFilter` fields. The mock API serves parents, the division filter, the grouped order, inbox
visits and a run's proposals. English and Dutch texts are included.

| Breaking change | Required action |
|---|---|
| The inbox marks proposals as new and records the person's inbox visit by default; before the first visit every proposal is new. | Set `markNew` or `recordVisit` to `false` where an inbox shows only part of the scope. |
| A run result offers decisions on the run's pending proposals to people who may decide them. | None; the server checks every decision as before. |
