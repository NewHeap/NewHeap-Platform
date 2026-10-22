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
