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

## NewHeap.Platform.AspNet.Common: authentication endpoints built once at host start

The endpoints that `UseNhAuthentication` maps are now built once, like the assistant's, and
routing, the authorization policy cache, link generation and API descriptions share them. Before,
every host start built them a second time on the first request.

| Breaking change | Required action |
|---|---|
| Conventions added to the authentication endpoints after the host started no longer apply. | None. Add route metadata in `IConfigurableAuthenticationEndpoint.Configure`. |

## NewHeap.Platform.Media.Http: media endpoints built once at host start

`MapNhMediaEndpoints` now builds the media endpoints once and gives routing, the authorization
policy cache, link generation and API descriptions the same endpoints, instead of building them
again on the first request.

| Breaking change | Required action |
|---|---|
| Conventions added to the group that `MapNhMediaEndpoints` returns after the host started no longer apply. | None. Configure the group before the host starts. |

## NewHeap.Platform.AspNet.Proxy: management API endpoints built once at host start

`MapProxyEndpoints` now builds the endpoints of `/newheap-proxy/api` once; the branch's routing and
the application-wide endpoints share them instead of each building their own.

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

## NewHeap.Platform.AI.AspNet.Common and NewHeap.Platform.AI.AspNet.Mvc: delegated reads under UseMvc

Fix. The delegated read restriction refuses a token whose target is unknown when it is
authenticated. `UseNewHeapPlatformAspNetCommon` serves controllers through the old `UseMvc`
routing, where a controller request has no endpoint, so in such a host every gateway read of a
headless agent run was refused and returned `api-bridge-unauthenticated`. When the gateway is
enabled, the API bridge now adds a global MVC authorization filter that runs first and
authenticates the delegated read scheme with the action that MVC executes as the target, in both
routing modes. A delegated read principal still reaches only the `GET` gateway action its token
names, with its invocation header, and a request whose target is not known is still refused. A
scheme that middleware authenticates before MVC, such as the default authentication scheme under
`UseMvc`, stays refused there; such a host routes its controllers with `UseEndpointRoutingOnly()`.

| Breaking change | Required action |
|---|---|
| With the gateway enabled, MVC authenticates the delegated read scheme before its other authorization filters for every `GET` request with the `X-NewHeap-AI-Invocation` header, and answers `401` when a delegated read principal reaches another action than the one its token names. | None. |

## NewHeap.Platform.AspNet.Common: controllers with endpoint routing only

New option, no breaking change. `NewHeapAspNetCommonOptionsBuilder.UseEndpointRoutingOnly()`
(`NewHeapAspNetCommonOptions.UseEndpointRoutingOnly`) routes controllers with endpoint routing only:
MVC keeps `EnableEndpointRouting` on, and `UseNewHeapPlatformAspNetCommon` routes before CORS and
authentication, maps controllers with `MapControllers` and no longer adds `UseMvc`. Controller
requests then carry endpoint metadata, so the authorization middleware, `[EnableCors]` and
`[DisableCors]` and other endpoint-metadata middleware apply to controllers, and the host builds one
route table instead of two. Map conventional routes in the `UseEndpoints` callback with
`MapControllerRoute`; combining the option with `UseMvc(Action<IRouteBuilder>)`, or turning
`EnableEndpointRouting` off in `ConfigureMvc`, fails at startup. The default is unchanged. New
applications opt in; an existing application first checks authorization handlers that read
`AuthorizationFilterContext`, filter-based authorization checks, generated links, Razor Pages and URL
rewriting in `UseMiddlewares` (guidance rule `nh-backend-platform-pipeline`).

## @newheap/platform-ai-chat: error codes and toNhAssistantError exported

Fix. The 0.15.0 notes name `NH_ASSISTANT_SERVER_ERROR_CODES` and `toNhAssistantError` for consumers,
but the main entry point did not export the first and exported the second only as the internal
`ɵtoNhAssistantError`. `@newheap/platform-ai-chat` now exports `NH_ASSISTANT_SERVER_ERROR_CODES`
(the failure codes of the back-end), `NH_ASSISTANT_ERROR_CODES` (those plus the client-side codes, the
keys a host with `translations: 'host'` needs under `nh-assistant.errors`) and `toNhAssistantError`,
which turns any failure into a code and translation key. `ɵtoNhAssistantError` remains as an alias.

| Breaking change | Required action |
|---|---|
| None; the exports are added. | Import `toNhAssistantError` instead of `ɵtoNhAssistantError`. |

## NewHeap.Platform.AI.Chat: the skill on the rule context

A rule handler registered once with `AddRuleHandler<THandler>(name, description)` can be named by
several data skills, or by the live and candidate versions of one, but could not tell which one it ran
for. `NhAssistantRuleContext` now carries `SkillId`, `SkillVersion` (the version of a skill defined as
data, `null` for a skill in code) and `SkillCandidate` (`true` in the run of a candidate version). All
three are `null` for the agent's own rules outside a skill.

| Breaking change | Required action |
|---|---|
| None; the properties are added. | Read the skill from the context instead of deriving it from the trigger or the inputs. |

## NewHeap.Platform.AI.Common, NewHeap.Platform.AI.Mcp and NewHeap.Platform.AI.Chat: whose work a budget reservation is

An application's budget manager could not tell an agent's work from a person's chat or an MCP
client's call. `NhAiBudgetRequest` now carries `AgentId`, `SkillId` and `RunId` where known, and
`Purpose`, one of `NhAiBudgetPurposes`: `chat`, `agent-run`, `consultation`, `proposal-execution`,
`mcp`, `ingestion` and `application`. The chat executor and the tool invoker fill them from the
invocation context (`agent-run` for a call that names an agent, `application` otherwise), ingestion
reserves as `ingestion`, and a call through the MCP export reserves as `mcp`.

The assistant still books the calls of its turns on its own daily ledger and gives the application's
manager only the calls outside turns. `NhAssistantBuilder.UseApplicationBudgetInTurns()` also
reserves every accepted call of a turn with the application's manager, with the turn's agent, skill,
run and purpose: `chat` for a person's turn, `agent-run` for a turn of an agent run,
`consultation` and `proposal-execution`. A refusal there ends the call as
`assistant-budget-exhausted`. Host start fails when a host calls it without a budget manager of its
own registered before `AddNewHeapAssistant`, because the default manager refuses every reservation.

| Breaking change | Required action |
|---|---|
| Budget requests from the chat executor, the tool invoker, ingestion and the MCP export carry the new properties, so two requests that were equal records before may differ now. | None, unless a budget manager compares whole requests; compare the counts and ids it needs. |

## NewHeap.Platform.AI.Common, NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: proposal guards at proposal time

Proposal guards checked a proposal only at revalidation, decision and execution, so rules and the
model proposed work that was already done, and those proposals waited until a later event or
decision closed them. `NhAiProposalGuardStages.Proposal` lets a guard check when a rule
(`NhAssistantRuleContext.ProposeAsync`) or the model proposes, before anything is recorded. Declare
it next to the other stages, for example
`AddProposalGuard<TGuard>(selectors, NhAiProposalGuardStages.All | NhAiProposalGuardStages.Proposal)`.
A stale result (`AlreadyDone`, `NotApplicable` or `Stale`) records no proposal, approval or
`ApprovalRequested` audit event: rules get `NhAssistantProposeOutcome.NotNeeded` with the guard's
reason code as `Code` and its explanation as the new `NhAssistantRuleProposal.Explanation`; the model
gets `assistant-proposal-not-needed` with `reason`, `kind` and `explanation` as its tool result, and
the tool call reads `not-executed`. A guard that throws at proposal time lets the proposal be
recorded, as a failing guard never closes one at the other stages. `GetKeysAsync` runs for every
guard of a tool, whatever its stages.

| Breaking change | Required action |
|---|---|
| `NhAssistantProposeOutcome` gains `NotNeeded`, and `NhAssistantErrorCodes` gains `ProposalNotNeeded` (`assistant-proposal-not-needed`, 400). | Handle `NotNeeded` where rules switch over the outcome; translate the code where the application shows assistant codes itself. |
| `AddProposalGuard` accepts `NhAiProposalGuardStages.Proposal`; `All` does not include it. | None for existing guards; add `Proposal` to the guards that should keep done work from being proposed. |

## @newheap/platform-ai-chat: proposals a guard found not needed

A tool call whose proposal a proposal guard found not needed reads `assistant-proposal-not-needed`.
`NH_ASSISTANT_SERVER_ERROR_CODES` includes it, and the bundled English and Dutch texts name it under
`nh-assistant.result-codes` and `nh-assistant.errors`.

| Breaking change | Required action |
|---|---|
| A new error code with texts under `nh-assistant.result-codes` and `nh-assistant.errors`. | Add both keys to translations a host provides itself (`translations: 'host'`). |

## NewHeap.Platform.AI.Chat, NewHeap.Platform.AI.Chat.AspNet, NewHeap.Platform.AI.Chat.SqlServer and NewHeap.Platform.AI.Chat.PostgreSql: agents and skills that are off

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

The kill switch is `INhAssistantAgentControl.SetKillSwitchAsync`, which
`POST admin/agents/{id}/kill-switch` uses. Engaged it stops the agent, as before. Releasing it never
switches on an agent that was off: an agent that was off when it stopped goes back off, one that is
off stays off, and every other agent resumes as before, a paused one included; skills keep their own
states. `NhAssistantKillSwitchDto.RunState` reports the state after the change. The
`AgentOffBeforeStop` migration adds the `OffBeforeStop` column to `AssistantAgent`, which a stop sets
for an agent that was off.

`StartsOff()` on `ConfigureAgent` and on `Skill(...)` (`NhAssistantAgentRunOptions.StartsOff`,
`NhAssistantSkillOptions.StartsOff`) deploys an agent or skill that waits for an administrator: it is
off the first time the application sees it, after which the stored state wins, also after a deploy.
`Enabled(false)` keeps meaning `configured-off`, which the administration cannot change. Every skill
in code gets a state row at its first start, which marks it as seen. An agent created in the
administration (`startsOff` on `NhAssistantAdminAgentInputDto` and `NhAssistantAgentRequest`) and a
new data skill (`startsOff` on `NhAssistantDataSkillRequest`) start off only when asked to;
`NhAssistantCodeRunSettingsDto` and `NhAssistantCodeSkillDto` report `StartsOff`. The state itself
uses the existing `RunState` columns.

| Breaking change | Required action |
|---|---|
| `INhAssistantAgentControl` has three new members, `SwitchOffAsync`, `SwitchOffSkillAsync` and `SetKillSwitchAsync`. | Implement them in your own implementations and test doubles. |
| Database schema change. | Apply the `AgentOffBeforeStop` migration of `NewHeap.Platform.AI.Chat.SqlServer` or `NewHeap.Platform.AI.Chat.PostgreSql`. |
| Releasing the kill switch of an agent that was off before it was engaged switches it off again instead of on. | Switch such an agent on with `ResumeAsync` or `POST admin/agents/{id}/state` `{ "state": "active" }`. |
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
skill and starts scenario agents and skills off with `startsOff`; its kill switch leaves an agent that
was off off when it is released, as the server does, and answers `runState`.

| Breaking change | Required action |
|---|---|
| `AgentRunState`, `AgentSkillState.runState` and `SetAgentStateRequest.state` include `off`. | Handle `off` where you switch over a state. |
| New translation keys `nh-assistant.admin.agents.state.off`, `off-hint`, `starts-off`, `action.off`, `action.switch-on`, `explain.off`, `explain.switch-on`, `confirm.off`, `confirm.switch-on`, `nh-assistant.admin.agents.skills.explain.off` and `switch-on`, `nh-assistant.admin.agent-editor.switch-on-now` and `switch-on-now-hint`, `nh-assistant.admin.run-config.skills.switch-on-now` and `switch-on-now-hint`, and `nh-assistant.errors.assistant-agent-off` and `assistant-skill-off`. | Applications with their own translation files add them. |

## NewHeap.Platform.AI.Chat, NewHeap.Platform.AI.Chat.AspNet, NewHeap.Platform.AI.Chat.SqlServer and NewHeap.Platform.AI.Chat.PostgreSql: data classification per agent and skill

`DataClassification(...)` on `ConfigureAgent` (`NhAssistantAgentRunOptions.DataClassification`) and on
`Skill(...)` (`NhAssistantSkillOptions.DataClassification`) classifies the data of an agent's model
calls, which were always `Internal` before. The most specific one wins: the skill's, then the
agent's, then `Internal`. It applies to every model call of the agent: its runs, people's turns in
its threads, its consultations and people's plain chat conversations with it, and the agent's own one
to its triage; it also classifies a skill's instructions and the run's hand-off calls. The profile resolution applies its existing check: a
profile that does not permit the classification is skipped for its fallbacks. When neither the
profile nor any of its fallbacks permits it, the turn fails at once with the new, non-transient code
`assistant-classification-not-permitted` (failure class `policy`, HTTP 400): a run is not retried,
because every attempt would be refused the same way. A data skill stores its own
(`dataClassification` on `NhAssistantDataSkillRequest` and `NhAssistantDataSkillDto`, one of
`NhAssistantDataClassificationNames`: `public`, `internal`, `confidential` or `restricted`; another
value is the field error `dataClassification: invalid`). An agent created in the administration
chooses its classification in its definition, next to its profile: `dataClassification` on `POST`
and `PUT admin/agents` (`NhAssistantAdminAgentInputDto.DataClassification`) and on
`NhAssistantAgentRequest`, with the same names and field error; omitted keeps it on update and a new
agent has none, so its calls are `Internal`. Its plain chats and, with run settings, its runs use it,
and `NhAssistantAdminAgentDto.DataClassification` shows the classification of every agent, from the
code for an agent in code.

Apply the `SkillDataClassification` and `AgentDataClassification` migrations of
`NewHeap.Platform.AI.Chat.SqlServer` or `NewHeap.Platform.AI.Chat.PostgreSql`: they add the nullable
`DataClassification` columns to `AssistantAgentSkillDefinition` and `AssistantAgent`.

| Breaking change | Required action |
|---|---|
| Database schema change. | Apply the `SkillDataClassification` and `AgentDataClassification` migrations. |
| `NhAssistantDataSkillRequest` has a new optional last parameter, `DataClassification`. | Recompile callers that construct it positionally. |
| A plain chat conversation with an agent that has a `DataClassification(...)` carries that classification instead of `Internal`, so its profile chain must permit it or the turn fails with `assistant-classification-not-permitted`. | Give such an agent a profile, or a profile with a fallback, that permits its classification. |
| `NhAssistantAdminAgentInputDto` and `NhAssistantAgentRequest` have a new optional last parameter, `DataClassification`; `NhAssistantAdminAgentDto` lists `dataClassification`. | Recompile callers that construct them positionally; clients that compare the agent's fields add `dataClassification`. |

## @newheap/platform-ai-chat: data classification of a skill

The skill form of the run configuration asks for the skill's data classification, with "The agent's
classification" as the default (Dutch: "Dataclassificatie", "De classificatie van de agent"), and
shows the server's `dataClassification` field error. The agent editor asks for the classification of
an agent created here, next to its model profile, with "Internal" as the default, sends
`dataClassification` only when it changes and shows its field error. `DataSkill`, `DataSkillRequest`,
`AdminAgentInput` and `AdminAgent` gain `dataClassification` of the new type
`AssistantDataClassification`. The mock backend of `@newheap/platform-ai-chat/testing` stores it for
data skills and agents created here and refuses an unknown value as the server does.

| Breaking change | Required action |
|---|---|
| New translation keys `nh-assistant.admin.run-config.skills.classification.*`, `nh-assistant.admin.agent-editor.classification` and `classification-hint`, and `nh-assistant.errors.assistant-classification-not-permitted`. | Applications with their own translation files add them. |

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: model profile of an agent created in the administration

An agent created in the administration always got the application's chat profile. It now chooses
its profile: `profile` on `POST` and `PUT admin/agents` (`NhAssistantAdminAgentInputDto.Profile`) and
`Profile` on `NhAssistantAgentRequest`. Only a profile of the run catalog's `Profiles` is accepted,
the registered profiles with chat, function calling and streaming that data skills already choose
from; another name is the field error `profile: not-found`, except the profile the agent has and the
chat profile, so a client that sends the agent back unchanged is not refused. Without it a new agent
keeps the chat profile and an update keeps the agent's profile; an agent in code takes its profile
from its code.
`NhAssistantAdminAgentDto.Profile` shows the profile of every agent. No migration: the agent's
existing `ProfileName` column holds it.

| Breaking change | Required action |
|---|---|
| `NhAssistantAdminAgentDto` lists `profile`; `NhAssistantAdminAgentInputDto` and `NhAssistantAgentRequest` have a new optional last parameter, `Profile`. | Recompile callers that construct them positionally; clients that compare the agent's fields add `profile`. |

## @newheap/platform-ai-chat: model profile of an agent created here

The agent editor offers the model profile of an agent created in the administration from the run
catalog's profiles, with "The application's chat profile" as the default for a new agent (Dutch:
"Modelprofiel", "Het chatprofiel van de applicatie"), and shows the server's `profile` field error.
`AdminAgentInput` gains `profile` and `AdminAgent` gains `profile`. The mock backend of
`@newheap/platform-ai-chat/testing` stores the profile and refuses one its run catalog does not
offer.

| Breaking change | Required action |
|---|---|
| New translation keys `nh-assistant.admin.agent-editor.profile`, `profile-default` and `profile-hint`. | Applications with their own translation files add them. |

## NewHeap.Platform.AspNet.Common: a heartbeat no longer stops its attempt over the attempt's own write

Fix. The heartbeat of a running background operation and the attempt's own writes (progress,
messages, checkpoints, results, signal suspension, fan-out and completion) take the same
per-operation transaction lock. A heartbeat that could not get that lock within
`TransactionLockTimeoutMilliseconds` stopped the attempt and rescheduled it as contention, also
when the holder was a slow write of that same attempt, for example one that compiled its first EF
query under load. A beat that falls due while the attempt's own write is in progress is now
skipped; the next beat waits for that write to end instead of skipping again. Contention with
another holder still reschedules the attempt without spending a retry, and a lost fencing token
still stops it. A skipped beat refreshes nothing, so an attempt whose own write never ends is
recovered by reconciliation after `StaleAttemptTimeout`.

| Breaking change | Required action |
|---|---|
| A heartbeat skips a beat while its attempt's own write holds the operation lock instead of rescheduling the attempt as contention. | None. |

## NewHeap.Platform.AI.Common, NewHeap.Platform.AI.Chat, NewHeap.Platform.AI.Chat.AspNet, NewHeap.Platform.AI.Chat.SqlServer and NewHeap.Platform.AI.Chat.PostgreSql: files in model turns

A tool can now give the model a file. Its result names the application's files next to its data:
data that implements the new `INhAiToolResultFiles`, or the new overload
`NhAiToolResult.Create(result, options, files)`, writes `files` with each `NhAiFileReference` (id,
name, media type, size and hash), never content; `NhAiToolResult.TryGetFiles` reads them and the
published result schema lists them. The application registers the new `INhAssistantFileSource`
with `UseFileSource<T>()`: `OpenAsync` opens a file for the actor of the turn
(`NhAssistantFileActor`) and returns `NhAssistantFile` with its content, media type, length, page
count and `NhAiDataClassification`, or refuses it with `assistant-file-not-found` or
`assistant-file-forbidden`; `StoreUploadAsync` (with `NhAssistantFileUpload`) stores a person's
upload. Without a source turns take no files (`assistant-files-unavailable`).

- `AcceptMediaTypes(...)` on `NhAiModelProfileBuilder` declares the file types a model reads itself
  (`NhAiModelProfile.AcceptedMediaTypes`, `AcceptsMediaType`, `NhAiMediaTypes`). A file of a declared
  type reaches the model as file content in a user message after the tool results, behind a marker
  that names it as external data and never instructions; another type is refused with
  `assistant-file-type-not-supported`, which names the type.
- `NhAssistantLimits` gains `MaxFileBytes` (20 MB), `MaxFilePages` (100), `MaxFilesPerTurn` (5) and
  `MaxFileTokensPerTurn` (100,000), refused with `assistant-file-too-large`,
  `assistant-file-too-many-pages`, `assistant-file-count-limit` and `assistant-file-token-limit`.
- A turn takes the highest classification of its files, agent and skill. `INhAiChatExecutor`
  classifies a model call as the most sensitive file it carries and counts the files' estimated
  tokens against the profile budget; a file no profile of the chain may receive is refused with
  `assistant-classification-not-permitted`.
- The chat assistant accepts files at once. An agent configured with `ConfigureAgent` and each of its
  skills accept none until `AcceptsFiles()` (`NhAssistantAgentRunOptions.FileLimits` and
  `AcceptsFiles`, the same on `NhAssistantSkillOptions`, with `NhAssistantFileLimits` that only lower
  the application's limits) or the administration switches them on: `acceptsFiles` on
  `NhAssistantAgentRunSettingsRequest`, `NhAssistantAgentRunSettingsDto`, `NhAssistantDataSkillRequest`
  and `NhAssistantDataSkillDto`, and shown on `NhAssistantCodeRunSettingsDto` and
  `NhAssistantCodeSkillDto`. Otherwise `assistant-files-not-accepted`.
- Messages and tool calls keep references and the SHA-256 of what the model read, never content. A
  later turn opens the files of the history again with its own actor. The new business audit events
  `FileRead` and `FileRefused` carry `FileId`, `FileMediaType` and `FileHash`.
- New error codes `assistant-files-unavailable`, `assistant-files-not-accepted`,
  `assistant-file-reference-invalid`, `assistant-file-not-found`, `assistant-file-forbidden`,
  `assistant-file-unavailable`, `assistant-file-too-large`, `assistant-file-too-many-pages`,
  `assistant-file-count-limit`, `assistant-file-token-limit` and `assistant-file-type-not-supported`,
  with their HTTP statuses in the shared table.

Apply the `AcceptsFiles` migration of `NewHeap.Platform.AI.Chat.SqlServer` or
`NewHeap.Platform.AI.Chat.PostgreSql`: it adds the `AcceptsFiles` column, false for existing rows, to
`AssistantAgentRunConfiguration` and `AssistantAgentSkillDefinition`.

| Breaking change | Required action |
|---|---|
| Database schema change. | Apply the `AcceptsFiles` migration. |
| The published result schema of every governed tool lists the optional `files`. | Update a client or test that compares the result schema's properties exactly. |
| `NhAssistantAgentRunSettingsRequest` and `NhAssistantDataSkillRequest` have a new optional last parameter, `AcceptsFiles`. | Recompile callers that construct them positionally; send `acceptsFiles` to keep files on when you save. |

## @newheap/platform-ai-chat: accepting files in the administration

The run settings form of an agent created in the administration and the skill form offer "Accepts
files" (Dutch: "Neemt bestanden aan"), off for a new agent or skill, and send `acceptsFiles`.
`AgentRunSettings`, `AgentRunSettingsRequest`, `DataSkill` and `DataSkillRequest` gain `acceptsFiles`;
`CodeRunSettings` and `CodeSkill` show it for agents and skills in code. The mock backend of
`@newheap/platform-ai-chat/testing` stores it. `NH_ASSISTANT_SERVER_ERROR_CODES` lists the eleven
file error codes, with English and Dutch texts.

| Breaking change | Required action |
|---|---|
| New translation keys `nh-assistant.admin.run-config.files.*` and `nh-assistant.errors.assistant-file*` for the file codes. | Applications with their own translation files add them. |

## NewHeap.Platform.AI.Common, NewHeap.Platform.AI.Chat, NewHeap.Platform.AI.Chat.AspNet, NewHeap.Platform.AI.Chat.SqlServer and NewHeap.Platform.AI.Chat.PostgreSql: reading tools for files

A file of a type the serving model does not read itself, such as a Word or Excel document, no longer
needs to be refused: a reading tool turns it into text, which the model reads behind the same marker as
untrusted external data. The application registers its own with the new
`AddFileReader<T>(readerId, reader => reader.AcceptMediaTypes(...).PermitDataClassifications(...))`
(`INhAssistantFileReader`, `NhAssistantFileReadRequest`, `NhAssistantFileReaderBuilder`); an
administrator sets an MCP server up as reader with the new `reader` setting (`mediaTypes`, the synced
`toolName` that receives `name`, `mediaType` and the base64 `content` of a file, and
`maxClassification`; `NhAssistantMcpReaders`, `NhAssistantMcpReaderInputDto` and
`NhAssistantMcpReaderDto` on `NhAssistantMcpServerInputDto` and `NhAssistantMcpServerDto`).

- The readers of the application come first, in registration order, then the MCP servers by id; the
  first that accepts the type and permits the file's classification reads it. A file above an MCP
  server's `maxClassification`, and a file for a server the turn may not use (its policy, headless use,
  a person's consent for `personal` or the initiator rule), is never sent; the code of the first reader
  that declares the type applies. Without a reader the file is still refused with
  `assistant-file-type-not-supported`.
- A reader that received the file and failed refuses it with the new code
  `assistant-file-reader-failed` (`502`), per file; no second reader receives it.
- The file and turn limits count the tokens of the text (one per four characters) instead of the
  file's pages; `INhAiChatExecutor` counts such text once, with the message text.
- The new `NhAssistantAuditEvent.FileReaderId` names the reader of `FileRead` and `FileRefused`
  (`mcp.<server>` with `McpConnectionOwner` for a server); `FileHash` is then the hash of the content
  the reader received.

Apply the `McpServerReaders` migration of `NewHeap.Platform.AI.Chat.SqlServer` or
`NewHeap.Platform.AI.Chat.PostgreSql`: it adds the nullable `ReaderMediaTypes`, `ReaderToolName` and
`ReaderMaxClassification` columns to `AssistantMcpServer`.

| Breaking change | Required action |
|---|---|
| Database schema change. | Apply the `McpServerReaders` migration. |
| `NhAssistantMcpServerDto` has the new property `reader`. | Update a client or test that compares the server's properties exactly. |

## @newheap/platform-ai-chat: MCP servers as readers of documents

The MCP server editor of a synced server offers "Reading documents" (Dutch: "Documenten lezen"): the
media types the server turns into text, the reading tool chosen from its synced tools and the highest
classification that may be sent to it, with a note that a file sent to the server leaves the
application. `McpServer` gains `reader` (`McpReader`) and `McpServerInput` the optional `reader`
(`McpReaderInput`); an empty `mediaTypes` removes the setting. The mock backend of
`@newheap/platform-ai-chat/testing` validates and stores it as the server does.
`NH_ASSISTANT_SERVER_ERROR_CODES` lists `assistant-file-reader-failed`, with English and Dutch texts.

| Breaking change | Required action |
|---|---|
| `McpServer` has the new required property `reader`. | Set `reader: null` where you construct an `McpServer`, such as in tests. |
| New translation keys `nh-assistant.admin.server-editor.reader.*` and `nh-assistant.errors.assistant-file-reader-failed`. | Applications with their own translation files add them. |

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: chat attachments

People can attach files to a chat message. The new endpoint `POST {prefix}/conversations/{id}/files`
takes one file as `multipart/form-data` (part `file`, header `X-NH-Assistant-Upload: 1`) under the
assistant's access policy, checks the conversation, the agent's policy, a file source, an agent that
accepts files, the accepted media types (whose content must start as files of that type do) and the
agent's `MaxFileBytes`, and then has `INhAssistantFileSource.StoreUploadAsync` store it. It returns
the file's `NhAiFileReference`, which the message carries in the new `files` of
`POST conversations/{id}/messages` (`NhAssistantSendMessageRequest.Files`).

- `ConfigureUploads(...)` on `NhAssistantBuilder` configures `NhAssistantUploadOptions`: the accepted
  media types (`AcceptMediaTypes`, by default PDF, PNG, JPEG, Word and Excel; none switches uploads
  off) and `DefaultClassification` (`Confidential`), which `StoreUploadAsync` receives as the new
  `NhAssistantFileUpload.Classification`.
- `NhAssistantAgentSummaryDto.Uploads` (`NhAssistantAgentUploadsDto`: accepted media types, largest
  file, most files of one message) in `GET status` and `GET agents` says what people may attach to
  each agent; `null` when nothing.
- The conversation shows a person's attachments as the new message part `file`
  (`NhAssistantFilePartDto`: name, media type and size), also in the live `message.created` event;
  never content, the application's id or a hash.
- A message with attachments in a busy agent thread is refused with `assistant-conversation-busy`
  instead of waiting for the next run.
- New error codes `assistant-file-upload-invalid` (400), `assistant-file-type-not-accepted` (415),
  `assistant-file-upload-refused` (422) and `assistant-file-upload-failed` (503).

| Breaking change | Required action |
|---|---|
| `NhAssistantSendMessageRequest` has a new optional last parameter, `Files`. | Recompile callers that construct it positionally. |
| Conversation views and live `message.created` events can contain parts of type `file`. | Clients that switch over part types handle or skip `file`. |

## @newheap/platform-ai-chat: attaching files in the chat

The composer offers an attach button when the server offers uploads for the agent
(`AgentSummary.uploads`, `AgentUploads`). `NhAssistantStore` checks every chosen file before it
uploads it (number, type, size; Office documents without a browser type by extension), creates the
conversation of a new chat first, follows the upload's progress and sends the stored files with the
message: `uploads`, `attachments` (`NhAssistantAttachment`), `attachedFiles`, `uploading`, `attach`,
`removeAttachment`, and `files` on `send` and `submit`; queued messages keep their files
(`NhAssistantQueuedMessage.files`). `NhAssistantComposerComponent` gains the inputs `uploads` and
`attachments` and the outputs `attach` and `removeAttachment`, shows progress and failures at the
control, and waits with sending while a file uploads. The thread shows a person's files by name, type
and size (`FilePart`). `NhAssistantApiService.uploadFile` uploads through the new `NH_ASSISTANT_UPLOAD`
(`NhAssistantUpload`, `NhAssistantUploadRequest`, by default `nhAssistantXhrUpload`, because `fetch`
reports no upload progress) with `NH_ASSISTANT_UPLOAD_HEADER`. `SendMessageRequest.files` and
`FileReference` follow the contract. The mock backend of `@newheap/platform-ai-chat/testing` answers
uploads and messages with files with the server's checks and codes, reports progress, lists its
`uploads` and refuses an upload through the scenario's `refuseUpload` (`NhAssistantMockUpload`).
`NH_ASSISTANT_SERVER_ERROR_CODES` lists the four upload codes, with English and Dutch texts.

| Breaking change | Required action |
|---|---|
| `MessagePart` includes `FilePart` (`type: 'file'`). | Code that switches over part types handles or skips `file`. |
| New translation keys `nh-assistant.composer.attach`, `attach-hint`, `attachments`, `remove-attachment`, `uploading`, `uploading-hint` and `attachment-errors.*`, `nh-assistant.thread.files` and `file-meta`, `nh-assistant.queue.files`, and `nh-assistant.errors.assistant-file-upload-invalid`, `assistant-file-type-not-accepted`, `assistant-file-upload-refused` and `assistant-file-upload-failed`. | Applications with their own translation files add them. |
| `NhAssistantIconName` includes `attach` and `file`. | Hosts that map every icon in `NH_ASSISTANT_ICONS` add classes for them. |

## NewHeap.Platform.AI.Chat, NewHeap.Platform.AI.Chat.SqlServer and NewHeap.Platform.AI.Chat.PostgreSql: concurrent joins of a conversation

Fix. A join of a conversation (by link, invitation or a thread's subject) checked whether the person
already took part, counted the participants and inserted the row without serializing with other
joins. Parallel requests of one person, such as a page that opens an agent's thread with several
requests at once, failed the insert on the primary key, which EF Core logged as an error
(`Microsoft.EntityFrameworkCore.Update`) before the assistant answered that the person already took
part; parallel joins of different people could pass `NhAssistantLimits.MaxParticipantsPerConversation`.
Joins of one conversation now take turns under the platform's transaction lock (`sp_getapplock` on
SQL Server, `pg_try_advisory_xact_lock` on PostgreSQL) and check, count and insert in one
transaction. A join that waits more than five seconds for the lock continues without it as before and
logs a warning. No migration and no action required.
