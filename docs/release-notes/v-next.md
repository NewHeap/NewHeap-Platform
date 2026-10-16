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

## NewHeap.Platform.AI.Chat, NewHeap.Platform.AI.Chat.AspNet, NewHeap.Platform.AI.Chat.SqlServer and NewHeap.Platform.AI.Chat.PostgreSql: agent skills

`ConfigureAgent(...).Skill(id, skill => ...)` gives one agent skills: parts of its role with their own
description, rules or model profile, instructions, tools within the agent's, token ceiling, daily
budget, approval policy and state, sharing the agent's identity, grants, thread, memory and
briefing. A run works in one skill, chosen by its trigger (`.Skill(id)` on event, scheduled and
webhook triggers), the agent's rules (`NhAssistantRuleResult.UseSkill`) or model triage
(`Triage(...)`, which may also end the run with nothing to do); the model sees only that skill's
tools and switches with the internal function `use_skill` at most twice, and people's turns start
without a skill. Runs record the skill and who chose it (`skill` and `skillChosenBy` on runs, the
skill on trigger and run audit events). `INhAssistantAgentControl` pauses, stops and resumes single
skills, `POST admin/agents/{id}/skills/{skillId}/state` does the same, and the agent's state lists
its skills with their states and today's usage. Agents without skills work as before.

| Breaking change | Required action |
|---|---|
| Migration `AgentSkills` adds `SkillId` and `SkillChosenBy` to `AssistantRun`, `SkillId` to `AssistantQueuedInput` and the table `AssistantAgentSkillState`. | Apply the migration. |
| `NhAssistantAgentState` and `NhAssistantAdminAgentDto` gain `Skills`; `NhAssistantRunDto` gains `Skill` and `SkillChosenBy`; `NhAssistantAuditEvent` gains `SkillId`. | None; audit sinks may record the skill. |
| An event joins only a queued run of the same skill, and a run claims only the inputs without a skill and those of its own skill. | None for agents without skills. |

## NewHeap.Platform.AI.Chat, NewHeap.Platform.AI.Chat.AspNet, NewHeap.Platform.AI.Chat.SqlServer and NewHeap.Platform.AI.Chat.PostgreSql: hand-offs between agents

An agent can hand work on a subject to another agent without a person.
`ConfigureAgent(target, agent => agent.AcceptHandOffs(subjectType, handOff => handOff.From(source)
.Description(...).NotifySource().Limits(maxPerSubjectPerDay)))` lists the agents that may hand work
to the target. A source's model hands off through the generated tool
`assistant.handoff.{agentId}.{subjectType}` when its tool selectors select it, only in its own runs;
a source's rules call `NhAssistantRuleContext.HandOffAsync(targetAgentId, subject, message,
payload)`. The target runs in its own thread as trigger `agent-handoff` without an initiator, under
its own grants, approval policies and budget, with the message as data from the source. A paused
target lets the work wait. Hand-offs stay within the source's scope key, a shadow run never hands off
to a live agent, a chain holds at most `NhAssistantLimits.MaxHandOffDepth` (2) hand-offs and never
returns to an agent in it, a source hands one subject to one target at most `Limits(...)` times a
day, and one run hands the same subject to the same agent once. With `NotifySource()` the source gets
an `agent-handoff-ended` input in the skill it handed off from, whose payload is an
`NhAssistantHandOffResult`: the outcome and proposal count, and the target's summary only when both
worked on the same subject and the target read nothing restricted with its own grant. Every hand-off
is audited as `AgentHandedOff` with source, target and outcome, never the message.

| Breaking change | Required action |
|---|---|
| Migration `AgentHandOffs` adds `RequestedByAgentId`, `RequestedByRunId`, `HandOffDepth` and `UsedRestrictedResults` to `AssistantRun` and the table `AssistantHandOff`. | Apply the migration. |
| `NhAssistantRunDto` gains `RequestedByAgentId`, `RequestedByConversationId` (only in a thread's run list, for a reader of the source thread) and `HandOffDepth`; `NhAssistantAuditEvent` gains `SourceRunId`; `NhAssistantAuditEventKind` gains `AgentHandedOff`. | None; audit sinks may record the source run. |
| The trigger ids `agent-handoff` and `agent-handoff-ended` are reserved. | Rename an application trigger with one of these ids. |

## NewHeap.Platform.AI.Chat, NewHeap.Platform.AI.Chat.AspNet, NewHeap.Platform.AI.Chat.SqlServer and NewHeap.Platform.AI.Chat.PostgreSql: agent evaluation

`ConfigureAgent(variant, agent => agent.ShadowOf(liveAgentId))` makes an agent a shadow variant of a
live agent: every event, scheduled and webhook trigger of the live agent also fires for the variant,
with the same subject, message, dedupe key and payload, and a trigger's skill when the variant
declares it. The variant keeps the shadow-mode guarantees and its own budget and state; publishers
read the live agent's receipts. `GET admin/agents/{id}/evaluation?variant=&from=&to=` compares the
variant with the live agent: it pairs runs that handled the same event or that a replay repeated,
matches their proposals by tool and arguments hash or by tool and objects, and returns per tool the
agreed, live-only and variant-only proposals with what people decided about the live ones (with
feedback categories), the runs, model calls, tokens and tool calls of both with averages and the 95th
percentile, and the skills both routed to. `GET .../evaluation/export` streams the pairs as JSON
Lines, audited as `AdminAgentEvaluationExported`. `POST admin/agents/{id}/replay` fires the recorded
runs of another agent in a period into a shadow agent, oldest first and against today's state, at
most 500 runs with one replay per variant, until the variant's state, budget or run limits stop it;
`GET admin/agents/{id}/replays` reports how far each got. Runs now record their model calls.

| Breaking change | Required action |
|---|---|
| Migration `AgentEvaluation` adds `ModelCalls` and `ReplayOfRunId` to `AssistantRun` and the table `AssistantAgentReplay`. | Apply the migration. |
| `NhAssistantAdminAgentDto` and `NhAssistantAgentState` gain `ShadowOf`; `NhAssistantAuditEventKind` gains `AdminAgentEvaluationExported` and `AdminAgentReplayStarted`. | None; audit sinks may record the new kinds. |
| A shadow variant receives its live agent's triggers. | None for agents without `ShadowOf`. |

## NewHeap.Platform.AI.Common, NewHeap.Platform.AI.AspNet.Mvc and NewHeap.Platform.AI.Chat: application map

An agent can start every turn with a generated map of the data the acting person may read:
`ConfigureAgentContext(agentId, context => context.UseApplicationMap())`. The `# Application map`
block follows the instructions and precedes the situation data, lists one line per resource with
its title, summary, operations and relations, and only resources that a tool the turn offers can
read. It is data, not instructions, is bounded by `NhAssistantLimits.MaxApplicationMapCharacters`
(4,000) with a note to use `search-resources`, is read once per turn within
`ApplicationMapTimeout` (2 seconds), and is not part of the prompt hash; tool audit events carry
`ApplicationMapHash`. The API bridge gateway is a map source (`INhAiApplicationMapSource`) with the
resources the person may use and relations by the `<resource>Id` convention, adjusted with
`Relate` and `IgnoreRelation` on the gateway builder; applications add sources with
`AddApplicationMapSource<T>()`. `ConfigureAgentContext` does not make an agent run on its own,
unlike `ConfigureAgent`.

| Breaking change | Required action |
|---|---|
| `NhAssistantAuditEvent` gains `ApplicationMapHash`. | None; audit sinks may record it. |
| Startup fails when a gateway `Relate` or `IgnoreRelation` names an unknown resource or filter field. | None until you add explicit relations. |

## NewHeap.Platform.AspNet.Common, NewHeap.Platform.AI.AspNet.Common, NewHeap.Platform.AI.AspNet.Mvc and NewHeap.Platform.AI.Chat: gateway reads in headless runs

A headless agent run can read through the API bridge gateway with the rights of the person who
started it, or of the person it works for when nobody did. `AddNewHeapPlatformAIDelegatedReads()`
issues a short-lived JWT of the host's own bearer scheme per call, bound to the invocation and to
the one gateway action it calls: the built-in issuer uses the new
`INhAuthenticationService.CreateDelegatedTokenAsync`, which carries the claims of the person's own
sign-in and is only issued while the new `MaySignInAsync` holds; other hosts supply
`INhAiDelegatedReadCredentialIssuer` with `UseIssuer<T>()`, and the library refuses an issued token
without its binding claims or that lives too long. Every JWT bearer scheme restricts such a token
to its gateway `GET` action with the matching `X-NewHeap-AI-Invocation` header; every other request
with it fails authentication. Agents opt in with `AllowGatewayReads()` on `ConfigureAgent` and
select the gateway's tools; they need an accountable person or service account. Their runs are
offered the four gateway tools only, calls record the new grant source `DelegatedRead`
(`delegated-read` in the API and the chat's tool card) with the person whose rights they used, and
only that person sees the result. The credential was security-reviewed before the release; the
plan lists the findings and their resolution.

| Breaking change | Required action |
|---|---|
| `NhAiGrantSource` gains `DelegatedRead`; tool calls report `delegated-read`. | Handle the new value where you map grant sources. |
| `INhAuthenticationService` gains `MaySignInAsync` and `CreateDelegatedTokenAsync` with default implementations. | None; implementations of your own refuse delegated tokens until they implement it. |
| The built-in `INhAiPrincipalFactory` refuses people who may not sign in (locked out, blocked, unconfirmed, second factor not enrolled where required). | None; delegation from such an initiator stops. |
| With `AddNewHeapPlatformAIDelegatedReads`, startup fails when any JWT bearer scheme sets `EventsType` or replaces its events after the registration. | Set `Events` in the scheme's own configuration. |
| The API bridge refuses route values that are empty or a dot segment with `api-bridge-validation`. | None. |
| The assistant's tool calls gain the column `ReadForActorId` (migration `DelegatedReadActor` for SQL Server and PostgreSQL). | Apply the migration; `RunMigrations` does it at startup. |
| `NhAiMvcBridgeDefaults.InvocationHeaderName` is now defined by `NhAiDelegatedReadDefaults.InvocationHeaderName`. | None; the value is unchanged. |
| The chat's shielded-result text no longer says the agent used its own rights. | Update overridden translations of `nh-assistant.tool-call.restricted`. |

## NewHeap.Platform.AI.Common, NewHeap.Platform.AI.AspNet.Common and NewHeap.Platform.AI.Chat: retrieved context in agent turns

Registered context sources reach agent turns:
`ConfigureAgentContext(agentId, context => context.UseRetrievedContext(retrieval =>
retrieval.Sources("project-documents")))`. Every turn of the agent retrieves once through
`INhAiContextResolver`, with its authorization, scope filter, ranking and budgets: a person's turn
with their latest message, a consultation with its question and a headless run with its inputs and
the subject's name, as the agent with the subject's scopes and division. The `# Retrieved context`
block is the last turn data, untrusted and outside the prompt hash, bounded by `MaxItems` (8) and
`MaxCharacters` (6,000, within `NhAssistantLimits.MaxRetrievedCharacters`, at most 24,000); a
resolver that fails or exceeds `RetrievalTimeout` (5 seconds) leaves the turn without it. Tool
audit events carry `RetrievedContextHash`, and a library trace sink logs the content-free
resolution trace. `NhAiContextRequest.SourceIds` limits a request to named sources, the data
format gains each item's `effectiveAt`, and `INhAiAgentInvocationContextFactory` creates the
context of an agent's own reads.

| Breaking change | Required action |
|---|---|
| `NhAssistantAuditEvent` gains `RetrievedContextHash`. | None; audit sinks may record it. |
| `INhAiContextFormatter`'s JSON gains `effectiveAt` per item. | None, unless you parse the formatted data. |
| Startup fails when `UseRetrievedContext` names a source that is not registered. | Register the source with `AddContextSource<T>()`. |

## @newheap/platform-ai-chat: agent state in the administration

The agent list shows each agent's state, "Active", "Paused", "Stopped" or "Off in configuration"
(Dutch: "Actief", "Gepauzeerd", "Gestopt", "Uit in configuratie"), with who changed it, when and
why, and the events that wait while it is paused. Pause, Stop and Resume ask for an optional reason
and explain what they do. The agent editor no longer has an enabled checkbox, and resetting a code
agent says that the state stays as it is. `NhAssistantAdminApiService` gains `getAgentState` and
`setAgentState`, and the mock backend of `@newheap/platform-ai-chat/testing` serves them. An agent
with skills lists them with their state, today's usage and budget, with pause, stop and resume per
skill (`setSkillState`); the run timeline shows the skill of a run and who chose it. `AgentRun`
gains `skill` and `skillChosenBy`, `AdminAgent` and `AgentState` gain `skills`. The run timeline
names the agent that handed a run its work ("Handed off by …", Dutch: "Overgedragen door …") with
a button that opens that agent's thread when the reader may; `NhAssistantRunTimelineComponent` and
`NhAssistantAgentThreadViewComponent` emit it through `openConversation`. `AgentRun` gains
`requestedByAgentId`, `requestedByConversationId` and `handOffDepth`. An agent with shadow variants
offers an evaluation (`NhAssistantAgentEvaluationComponent`): choose a variant and a period, read
the runs, the proposals per tool with the live decisions, cost and routing, open the threads of the
differences in the assistant panel, replay the period into the variant and export the comparison.
`NhAssistantAdminApiService` gains `getAgentEvaluation`, `exportAgentEvaluation`, `startAgentReplay`
and `getAgentReplays`; `AdminAgent` and `AgentState` gain `shadowOf`, and the mock backend serves
the new endpoints.

| Breaking change | Required action |
|---|---|
| `AdminAgentInput.isEnabled` is optional and deprecated; the server ignores it. `AdminAgent` gains `runState`, `stateChangedAt`, `stateChangedBy`, `stateReason` and `waitingInputs`. | Stop sending `isEnabled`; change the state with `setAgentState`. |
| New translation keys `nh-assistant.admin.agents.state.*`, `nh-assistant.admin.agents.reset-keeps-state`, `nh-assistant.admin.agent-editor.state-hint` and `nh-assistant.errors.assistant-agent-stopped`, `assistant-agent-paused`, `assistant-agent-configured-off`, `assistant-run-token-limit` and `assistant-skill-*`, `nh-assistant.admin.agents.skills.*` and `nh-assistant.agent-runs.run.skill` and `skill-chosen-by.*`, `nh-assistant.agent-runs.run.handed-off-by` and `open-source-thread`, `nh-assistant.errors.assistant-handoff-*`, `nh-assistant.admin.agents.evaluation`, `nh-assistant.admin.evaluation.*` and `nh-assistant.errors.assistant-replay-not-shadow`, `assistant-replay-active` and `assistant-evaluation-not-variant`; `nh-assistant.admin.agent-editor.enabled` is removed. The run timeline shows a run stopped at its token ceiling with that text. | Applications with their own translation files add and remove them. |
