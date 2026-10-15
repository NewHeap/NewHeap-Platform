# v-next

## NewHeap.Platform.AI.Chat, NewHeap.Platform.AI.Chat.AspNet, NewHeap.Platform.AI.Chat.SqlServer and NewHeap.Platform.AI.Chat.PostgreSql: pause, stop and resume agents

Agents have a run state besides their definition: `active`, `paused` or `stopped`, with who changed
it, when and why, and `configured-off` for an agent disabled in code with `Enabled(false)`.
`INhAssistantAgentControl` (`GetAsync`, `PauseAsync`, `StopAsync`, `ResumeAsync`) changes it from
the application's own code; `GET` and `POST admin/agents/{id}/state` and the administration do the
same. A paused agent starts no runs: events, webhooks, manual starts and requests wait per thread
(receipt outcome `waiting`, deduplicated by their dedupe key), schedules skip their ticks, running
runs finish, approved proposals execute and people keep chatting; resuming starts one run per thread
that sees all waiting events. Stopping is the kill switch: active runs are cancelled, their pending
proposals withdrawn, waiting events end, approved proposals are held, and triggers and chat are
refused. Leaving the stopped state queues the held executions at once. `When` on `AddTrigger` and
`AddWebhookTrigger` filters events before any other lambda runs. A stopped or code-disabled agent's
triggers evaluate no lambda and are audited once per publish call, and with
`NewHeap:AI:Assistant:Enabled` off, triggers, schedules, runs and execution queuing stop as well.
The maintenance job now finds held executions in its database query, consultation proposals
included, so queued executions no longer hide the ones behind them.

| Breaking change | Required action |
|---|---|
| `isEnabled` in `POST admin/agents` and `PUT admin/agents/{id}` is accepted and ignored until the next major version; a new agent starts active, and a reset keeps the state. | Pause, stop and resume with `POST admin/agents/{id}/state` or `INhAssistantAgentControl`. |
| Triggers of a stopped agent are rejected with `assistant-agent-stopped` and of a code-disabled agent with `assistant-agent-configured-off`, instead of `assistant-agent-disabled`; runs end with the same codes. | Handle the new codes where `assistant-agent-disabled` was handled. |
| An agent disabled in code with `Enabled(false)` is no longer offered for chat, and its approved proposals are held like a stopped agent's. | Remove `Enabled(false)` from agents that people should keep chatting with. |
| With `NewHeap:AI:Assistant:Enabled` off, `PublishAsync` and `StartAsync` return `rejected` with `assistant-disabled`, scheduled ticks are skipped, queued runs end and maintenance queues no executions and sends no reminders. | None; enable the assistant to let agents work. |
| The kill switch no longer changes the agent's definition version, and `AdminAgentRunsResumed` reports `resumed-{count}` instead of `enabled`. | Read the state from `runState`, not from the version or `isEnabled`. |
| Migration `AgentState` adds the state columns to `AssistantAgent` and `EventKey` and `InitiatorActorId` to `AssistantQueuedInput`; an agent whose kill switch was engaged reads as stopped. | Apply the migration. |

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: subject briefings and a token ceiling per run

`AddSubjectBriefing<TProvider>()` registers an `INhAssistantSubjectBriefingProvider` per subject
type. Every model turn of an agent about the subject reads its sections as a `<subject-briefing>`
block after the instructions and before the continuity: headless runs, people's turns in the
subject's thread and consultations. A rule run that hands over to the model now passes its reason
as the briefing's first section instead of a separate system message. Briefings are bounded by
`NhAssistantLimits.MaxBriefingCharacters` (8,000 by default, at most 32,000) and
`BriefingTimeout` (5 seconds); a failing or slow provider leaves the turn without a briefing.
`ConfigureAgent(...).MaxTokensPerRun(tokens)` stops a run before its next model call once it used
that many tokens; the run fails with failure class `budget` and `assistant-run-token-limit`, keeps
its proposals and gets a new allowance when it is resumed.

| Breaking change | Required action |
|---|---|
| The hand-over reason of a rule run reaches the model inside the briefing block, titled "What the agent's rules found". | None; tests that look for the old system message look for the reason in the briefing. |
| A run that hits `MaxTokensPerRun` ends `failed` with `assistant-run-token-limit`; no ceiling is set by default. | Set a ceiling per agent that runs on events, and handle the code where failed runs are shown. |
| No migration. | None. |

## @newheap/platform-ai-chat: agent state in the administration

The agent list shows each agent's state, "Active", "Paused", "Stopped" or "Off in configuration"
(Dutch: "Actief", "Gepauzeerd", "Gestopt", "Uit in configuratie"), with who changed it, when and
why, and the events that wait while it is paused. Pause, Stop and Resume ask for an optional reason
and explain what they do. The agent editor no longer has an enabled checkbox, and resetting a code
agent says that the state stays as it is. `NhAssistantAdminApiService` gains `getAgentState` and
`setAgentState`, and the mock backend of `@newheap/platform-ai-chat/testing` serves them.

| Breaking change | Required action |
|---|---|
| `AdminAgentInput.isEnabled` is optional and deprecated; the server ignores it. `AdminAgent` gains `runState`, `stateChangedAt`, `stateChangedBy`, `stateReason` and `waitingInputs`. | Stop sending `isEnabled`; change the state with `setAgentState`. |
| New translation keys `nh-assistant.admin.agents.state.*`, `nh-assistant.admin.agents.reset-keeps-state`, `nh-assistant.admin.agent-editor.state-hint` and `nh-assistant.errors.assistant-agent-stopped`, `assistant-agent-paused`, `assistant-agent-configured-off` and `assistant-run-token-limit`; `nh-assistant.admin.agent-editor.enabled` is removed. The run timeline shows a run stopped at its token ceiling with that text. | Applications with their own translation files add and remove them. |
