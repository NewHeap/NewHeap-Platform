# v-next

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
