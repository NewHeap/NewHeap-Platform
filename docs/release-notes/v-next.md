# v-next

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: a missing tenant is no longer silent

Since 23.0.0 agent work stays in the caller's tenant, and a person without a tenant reaches only work
stored without one. An application that built `NhAssistantDecidingPerson` without the person's tenant
compiled as before, but its digests counted zero and its decisions, decision facts and subject work
read the work as unknown. The tenant is now a required parameter, and a caller without a tenant who
addresses one thread, run, proposal or subject stored under a tenant is logged as a warning by
`NhAssistantTenancy`, at most once an hour per path, with the path and the subject type only. The
answer stays the same: the work reads as unknown, as for a caller of another tenant. Listings and
callers of another tenant log nothing. Hosts without tenants see no change in behavior.

| Breaking change | Required action |
|---|---|
| `NhAssistantDecidingPerson(User, ActorId, TenantId)` requires `TenantId`; the default `null` is gone. | Pass the tenant the person signs in with, as the context resolver gives it; pass `null` explicitly only in a host without tenants or for its operators. |

## @newheap/platform-ai-chat: agent administration by agent page

The administration opens on Agents and lists every agent once, with one status, where it works, where
it comes from and what needs attention; a row opens the agent's page, whose header holds the status
changes, "Test in chat", "Run now" and "More" (export, reset to code, delete), and whose tabs Overview,
Build, Rollout and Activity replace the separate editor, run configuration, runs, feedback and
evaluation views. Every feature stays; hosts can keep the open agent in their URL with `location` and
`locationChange`. `nh-assistant-panel` gains `mode="page"` for a chat in a page of its own.

| Breaking change | Required action |
|---|---|
| `NhAssistantAdminComponent.initialTab` defaults to `agents` instead of `context`, and the sections read Agents, Tools & connections, Application context. | Pass `initialTab="context"` to keep opening on the application context. |
| The agent cards and their buttons are gone: state, shadow, account, run now, export, reset and delete moved to the agent's page, the editor and the run configuration to its Build tab, runs and feedback to Activity, shadow variants and evaluation to Rollout. | Update end-to-end tests and documentation that clicked card buttons: open the agent with its list row (`[data-agent-row]`), then the tab (`[data-agent-tab]`) or the header menus (`[data-status-menu]`, `[data-more]`). |

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: code settings in the administration

`NhAssistantCodeRunSettingsDto` also carries what an agent's code sets for its runs, read only, so the
administration's guardrails show it: `SkillsInChat`, `AllowQuestions`, `UsesMemory`, `TriageProfile`,
`RetryCount`, `MaxConcurrentRuns`, `MaxRunsPerSubjectPerDay`, `DailyToolCalls`, `DailyTokens`,
`MaxTokensPerRun`, `MaxConsultationsPerTurn`, `ElevatedReads` and `AllowGatewayReads`.

| Breaking change | Required action |
|---|---|
| A record comparison with `new NhAssistantCodeRunSettingsDto(...)` now also compares the new init properties, whose values come from the agent's code, such as `RetryCount = 3`. | Set the properties the agent's code configures in the expected record, or compare the four constructor values. |
