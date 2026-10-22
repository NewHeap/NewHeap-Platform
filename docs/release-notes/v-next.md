# v-next

## NewHeap.Platform.AI.Chat.AspNet: assistant endpoints built once at host start

`MapNewHeapAssistant` built every assistant endpoint twice when a host started: the authorization
middleware's policy cache reads the application-wide endpoints while the pipeline is built, and the
routing middleware reads them again through a composite of its own on the first request, while a
route group creates its minimal API endpoints anew on every read. The assistant's route groups now
build their endpoints once and give every reader the same endpoints. The first request no longer
pays for a second build (about 180 ms for the 110 endpoints in a Debug test host).

| Breaking change | Required action |
|---|---|
| Routing, the authorization policy cache, link generation and API descriptions share the assistant endpoints built on the first read; conventions added to the group that `MapNewHeapAssistant` returns after the host started no longer apply. | None. Configure that group before the host starts. |

## NewHeap.Platform.AI.Common, NewHeap.Platform.AI.AspNet.Common and NewHeap.Platform.AI.AspNet.Mvc: tool catalogs validated after the endpoints are registered

Fix. The startup validation of the tool catalogs (the platform's catalog checks, the API bridge
validator and the MCP export check) ran as hosted services. A web application in the minimal
hosting model starts its hosted services before it builds its pipeline, so reading the API
bridge catalog built ApiExplorer's descriptions while the application's endpoints were not yet
registered. ApiExplorer keeps those descriptions for the life of the process, so ApiExplorer and
OpenAPI documents missed every minimal API endpoint, and the application-wide endpoint data source
was initialized empty and built all endpoints again for every data source added afterwards. A
host with a server now runs these checks once, after its application pipeline is configured and
before the server starts; a host without a server still runs them while its hosted services
start. An invalid catalog fails the host's start with the same exception as before.

| Breaking change | Required action |
|---|---|
| In a web host the AI tool catalog checks run while the web host service builds the pipeline instead of at their own place among the hosted services. A host that captures startup errors (`CaptureStartupErrors`, as under IIS) serves its startup error page for an invalid catalog instead of stopping. | None. Do not read the API bridge catalog or ApiExplorer from your own hosted services before the pipeline is built. |

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: agents and skills that are off

An agent and a skill have a fourth state next to active, paused and stopped: **off**
(`NhAssistantAgentRunStates.Off`). `INhAssistantAgentControl.SwitchOffAsync` and
`SwitchOffSkillAsync`, `POST admin/agents/{id}/state` and `POST admin/agents/{id}/skills/{skillId}/state`
with `{ "state": "off" }` and the administration switch it off; `ResumeAsync`, `ResumeSkillAsync` and
`active` switch it on. While an agent is off its events (data triggers included), schedule ticks
(data schedules included), webhooks and follow-up runs are ignored before any of their lambdas runs:
the receipt reads `ignored` with `NhAssistantTriggerReasons.AgentOff` (`agent-off`), nothing waits and
nothing is audited, as for `filtered`. A hand-off to it is refused with `assistant-agent-off` and a
replay into an off variant stops with it. Running and queued runs finish, open proposals stay
decidable and executable, and people still chat with it and start it by hand or through a request.
Switching it off ends the inputs that waited from an earlier pause, so switching it on starts no run
for anything that happened meanwhile. A skill that is off ignores the triggers that name it
(`skill-off`); a run that rules route to it ends with `assistant-skill-off`, and triage and
`use_skill` do not offer it. Switching off is audited as `NhAssistantAuditEventKind.AdminAgentSwitchedOff`
(`off`), switching on as `AdminAgentRunsResumed`.

`StartsOff()` on `ConfigureAgent` and on `Skill(...)` (`NhAssistantAgentRunOptions.StartsOff`,
`NhAssistantSkillOptions.StartsOff`) deploys an agent or skill that waits for an administrator: it is
off the first time the application sees it, after which the stored state wins, also after a deploy.
`Enabled(false)` keeps meaning `configured-off`, which the administration cannot change. Every skill
in code gets a state row at its first start, which marks it as seen. An agent created in the
administration (`startsOff` on `NhAssistantAdminAgentInputDto` and `NhAssistantAgentRequest`) and a
new data skill (`startsOff` on `NhAssistantDataSkillRequest`) start off only when asked to;
`NhAssistantCodeRunSettingsDto` and `NhAssistantCodeSkillDto` report `StartsOff`. No migration: the
state uses the existing `RunState` columns.

| Breaking change | Required action |
|---|---|
| `INhAssistantAgentControl` has two new members, `SwitchOffAsync` and `SwitchOffSkillAsync`. | Implement them in your own implementations and test doubles. |
| `NhAssistantAgentRunStates.IsStored` accepts `off`, and `RunState` of agents and skills, `NhAssistantAgentState` and `NhAssistantSkillState` can read `off`. | Treat `off` in code that switches over the state. |
| `NhAssistantAuditEventKind` has the new value `AdminAgentSwitchedOff` (62). | Map it in business audit sinks that switch over the kinds. |
| At the first start on this version every skill in code gets a state row; a skill deployed before that gets `StartsOff()` in the same deploy starts off once. | Add `StartsOff()` to an existing skill only when it should go off. |

## @newheap/platform-ai-chat: switching agents and skills off

The agent list offers "Switch off" for an agent and each skill, and "Switch on" for one that is off
(Dutch: "Uitzetten", "Aanzetten"), each with an optional reason and an explanation; the state reads
"Off" ("Uit"), says that triggers are ignored while people can still chat with the agent and start it
by hand, and that it started off in code when no one changed it yet. "Run now" stays available for an
agent that is off. The agent editor and the skill form ask whether a new agent or skill is switched on
now; unticked, they send `startsOff: true`. `AgentRunState` and the skill and request states gain
`off`, `AdminAgentInput` and `DataSkillRequest` gain `startsOff`, and `CodeRunSettings` and
`CodeSkill` gain `startsOff`. The mock backend of `@newheap/platform-ai-chat/testing` serves the
state, creates agents and data skills off with `startsOff`, refuses `startsOff` on a change of a
skill and starts scenario agents and skills off with `startsOff`.

| Breaking change | Required action |
|---|---|
| `AgentRunState`, `AgentSkillState.runState` and `SetAgentStateRequest.state` include `off`. | Handle `off` where you switch over a state. |
| New translation keys `nh-assistant.admin.agents.state.off`, `off-hint`, `starts-off`, `action.off`, `action.switch-on`, `explain.off`, `explain.switch-on`, `confirm.off`, `confirm.switch-on`, `nh-assistant.admin.agents.skills.explain.off` and `switch-on`, `nh-assistant.admin.agent-editor.switch-on-now` and `switch-on-now-hint`, `nh-assistant.admin.run-config.skills.switch-on-now` and `switch-on-now-hint`, and `nh-assistant.errors.assistant-agent-off` and `assistant-skill-off`. | Applications with their own translation files add them. |

## NewHeap.Platform.AI.Chat, NewHeap.Platform.AI.Chat.AspNet, NewHeap.Platform.AI.Chat.SqlServer and NewHeap.Platform.AI.Chat.PostgreSql: data classification per agent and skill

`DataClassification(...)` on `ConfigureAgent` (`NhAssistantAgentRunOptions.DataClassification`) and on
`Skill(...)` (`NhAssistantSkillOptions.DataClassification`) classifies the data of an agent's model
calls, which were always `Internal` before. The most specific one wins: the skill's, then the
agent's, then `Internal`. It applies to the model calls of the agent's runs, people's turns in its
threads and its consultations, and the agent's own one to its triage; it also classifies a skill's
instructions and the run's hand-off calls. The profile resolution applies its existing check: a
profile that does not permit the classification is skipped for its fallbacks, and the call is
refused as `assistant-model-unavailable` when none permits it. A data skill stores its own
(`dataClassification` on `NhAssistantDataSkillRequest` and `NhAssistantDataSkillDto`, one of
`NhAssistantDataClassificationNames`: `public`, `internal`, `confidential` or `restricted`; another
value is the field error `dataClassification: invalid`).

Apply the `SkillDataClassification` migration of `NewHeap.Platform.AI.Chat.SqlServer` or
`NewHeap.Platform.AI.Chat.PostgreSql`: it adds the nullable `DataClassification` column to
`AssistantAgentSkillDefinition`.

| Breaking change | Required action |
|---|---|
| Database schema change. | Apply the `SkillDataClassification` migration. |
| `NhAssistantDataSkillRequest` has a new optional last parameter, `DataClassification`. | Recompile callers that construct it positionally. |

## @newheap/platform-ai-chat: data classification of a skill

The skill form of the run configuration asks for the skill's data classification, with "The agent's
classification" as the default (Dutch: "Dataclassificatie", "De classificatie van de agent"), and
shows the server's `dataClassification` field error. `DataSkill` and `DataSkillRequest` gain
`dataClassification` of the new type `AssistantDataClassification`. The mock backend of
`@newheap/platform-ai-chat/testing` stores it and refuses an unknown value as the server does.

| Breaking change | Required action |
|---|---|
| New translation keys `nh-assistant.admin.run-config.skills.classification.*`. | Applications with their own translation files add them. |
