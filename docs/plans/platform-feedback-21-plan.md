# Platform Feedback on 21.0.0 Plan

A consuming application upgraded to NuGet 21.0.0, `@newheap/platform-common` 3.0.0 and
`@newheap/platform-ai-chat` 0.20.0 and reported what is still missing. Eight findings come from that
report; four are the follow-ups that the lanes of trains 21 to 23 left (LB8, LC12, MP2, CS6 and CS9 in
`platform-feedback-20-plan.md`). This plan records one choice per finding. It is a plan, not usage
documentation: the guidance rules, the release notes and the sample cases are authoritative once it
is built.

## Status

Decided by Lars on 2026-10-26: FB1 to FB15, every one as advised (option (a)). The decisions are summarized
below; the sections under "Choices" keep the options that were weighed. Not built yet: FB15 plans train 24
(FB1, FB12, FB13) and train 25 (FB2 to FB11).

## Decisions

| # | Decision |
|---|---|
| FB1 | **The Request Delegate Generator in `NewHeap.Platform.AI.Chat.AspNet`**, measured before and after; a handler it cannot take stays on the factory and is named. |
| FB2 | **`INhAssistantAgentRunLookup.FindRunAsync(runId, tenantId)`** returns `NhAssistantAgentRunFacts` with status, failure, proposal count, summary and result, without a person check; the event lookup stays. |
| FB3 | **Shadow proposals store their guard keys** and follow the policy's `Skip` or `Replace` among themselves within their lifetime; a replaced one becomes `shadow-superseded`. |
| FB4 | **The approval records the UI culture of the turn that approved it** (migration), and the chat report and deadline posts use it; otherwise the default culture. |
| FB5 | **`ConversationId` and `Channel` on `NhAiInvocationContext`, a public `INhAiTurnPerson`, and `INhAssistantConversations` (create, get, cancel) with `INhAssistantConversationTurns.AnswerAsync`.** |
| FB6 | **A question keeps `waiting-for-approval`**; the notifier sends a question notice of its own to the addressee. |
| FB7 | **A new message closes a waiting question or approval that passed its lifetime** as a late answer or decision would, then starts its turn; no job. |
| FB8 | **`run.ChatOnly()`**: chat settings without any subject, start, trigger, hand-off or replay; conflicting run settings refuse host start. |
| FB9 | **An omitted `mcpServerIds` keeps the assignments**, new `PUT`/`DELETE admin/mcp-servers/{id}/agents/{agentId}`, and the editor assigns one by one; a breaking row for the omitted list. |
| FB10 | **Reviewed settings for a tool no sync has seen** are stored as a `missing` row without schema hash; the first sync that sees it makes it `available` with them. |
| FB11 | **Promote with `AsPackageVersion`** applies a package-linked variant's parts to the live agent with the import's rules, relinks it to that package version and deletes the variant and its links. |
| FB12 | **The runner's failure path checks the durable cancellation**: a handler error during a requested cancellation is `Cancelled`, logged as a warning, not retried. |
| FB13 | **The background-operations `HubPath` may be any absolute application path** outside the API prefixes; the default stays. |
| FB14 | **Nothing waits for the application's evidence.** |
| FB15 | **Two trains**: 24 with FB1, FB12, FB13; 25 with FB2 to FB11 in four lanes (runs and decisions, chat, agents and packages, MCP). |

## Rules for all of it

- Every change is opt-in or compatible unless its choice says otherwise. A breaking change gets a
  `Breaking change | Required action` row.
- Every change follows the repository rules:
  - code and migrations for SQL Server and PostgreSQL where persistence changes;
  - tests with authorization negatives;
  - a release-notes section per release unit;
  - guidance and consumer guide updates;
  - an executable sample case;
  - English and Dutch texts for Angular parts.
- Nothing names the consuming application.

## Choices

Each choice lists the options that were weighed. "(advice)" marks the recommendation, which Lars followed for all
of them.

### A. Host start

#### FB1. The assistant's request delegates are compiled in every host

`MapNewHeapAssistant` maps 139 minimal API endpoints, most with endpoint filters. Since dfe13e18 each
is built once per host instead of twice (`NhEndpointsBuiltOnce`), but ASP.NET Core's
`RequestDelegateFactory` still compiles every request delegate (expression trees to dynamic methods)
when a host starts. The report measured about 300 ms of CPU per host, half of all busy time in its API
tests. No NewHeap project sets `EnableRequestDelegateGenerator`, and a host cannot map part of the
surface.

- **(a) The Request Delegate Generator in `NewHeap.Platform.AI.Chat.AspNet` (advice).** The delegates
  are generated at build time, so a host start compiles none of them, and the endpoints become fit for
  trimming. Nothing changes for applications. A handler the generator cannot take stays on the factory
  and is named in the lane's choices. A test proves that the assistant's endpoints are generated, and a
  measurement before and after goes into the release note. Other libraries with minimal APIs (media,
  authentication, proxy) follow only when a measurement shows the same cost.
- **(b) Map parts of the surface.** `MapNewHeapAssistant(prefix, o => o.Parts(...))`, for example the
  administration alone. It only helps hosts that leave parts out, and a host that leaves out a part the
  front end calls fails at run time.
- **(c) Both.**

### B. Runs and decisions

#### FB2. A run's result for the application

`INhAssistantAgentRunLookup` names the run that an event started, with its status only
(`NhAssistantTriggerEventRun.RunStatus`). Its summary, result, proposal count and failure code are in
the store (`AssistantRun`) or behind the person-scoped `GET runs/{id}`, so the application reads the
table.

- **(a) A server read of a run by its id (advice).** `INhAssistantAgentRunLookup.FindRunAsync(runId,
  tenantId)` returns `NhAssistantAgentRunFacts`: agent, subject, thread, shadow, trigger, kind, status,
  failure class and code, attempt, proposal count, summary, the result with `ResultTruncated`, and the
  times. It checks no person's access, as the lookup does, and the same tenant rule applies. The result
  is model output: the documentation says so, as `NhAssistantRunResultDto` does. The event lookup stays
  as it is.
- **(b) The facts on `NhAssistantTriggerEventRun`.** Every event lookup then reads the result, also when
  the caller wants only the status, and a run that no event started stays unreadable.
- **(c) Both.**

#### FB3. Shadow proposals that replace each other

Shadow proposals store no object keys, so `CheckShadowAsync` only knows the identity rule (tool and
canonical arguments, LS9). A shadow proposal for the same objects with other arguments is recorded
next to the older one, where a live proposal would replace it or be skipped by the policy's
`NhAiDuplicateProposals`. An agent whose arguments change every day, such as a due date of today, piles
up shadow proposals and makes the evaluation unfair.

- **(a) Object keys on shadow proposals and the policy's rule among them (advice).**
  - A shadow proposal stores its guard keys as a live one does (`AssistantApprovalKey`, no migration).
  - Within its proposal lifetime, `Skip` reports the newer one as `AlreadyOpen`, and `Replace` closes
    the older one with a new status `shadow-superseded`.
  - The rejection cooldown does not apply, because nobody decides shadow proposals.
  - Revalidation, inboxes and counts keep ignoring shadow proposals.
  - The evaluation counts a replaced shadow proposal once, as the live agent's would have counted.
- **(b) Only `Skip`.** A newer shadow proposal for the same objects is never recorded. It is simpler, but
  the evaluation then shows the oldest arguments, where the live agent would show the newest.
- **(c) Leave it.** The application keeps its own shadow memory per object.

#### FB4. A chat report in the conversation's language

The application message that NewHeap posts into a chat for an `AlsoInline` report, or for its deadline,
starts with an outcome line in the application's default request culture (`NhAssistantRunCulture`,
DN8). A Dutch reader of a Dutch chat then gets an English first line.

- **(a) The culture of the turn that handed the proposal off (advice).** The approval records the UI
  culture of the person's turn that approved it (a new column, migrations on both providers). The
  report and the deadline post use that culture, and the default culture when there is none, such as for
  older approvals. Agent threads keep the default culture.
- **(b) Every reader in their own language.** The line is stored in every supported UI culture, as
  presentations are, and the client picks one. Application messages then need localized text: a new
  message part in the API and the Angular client, for one line.
- **(c) Leave it.** The application sets its default request culture.

### C. Chat

#### FB5. A service turn's person, channel and conversation

`INhAssistantConversationTurns` runs a turn without a request (LS19), but application code cannot learn
whom it works for, which channel started it or which conversation it is in:
- `NhAiPersonInvocation` is internal.
- `INhAssistantTurnContextProvider` gets the invocation context only.
- Tools and policies that read the person or the conversation from the request find nothing.

The service also only sends and decides. Creating, reading, cancelling and answering still need the
endpoints.

- **(a) The facts on the invocation context, a person accessor and the missing services (advice).**
  - `NhAiInvocationContext` gains additive `ConversationId` and `Channel`, set in every assistant turn:
    the channel is `null` for the endpoints' own turns. The turn context provider, tools and policies
    then see them without a signature change.
  - A public `INhAiTurnPerson` gives the turn's person as a principal, actor and tenant: the request's
    user in a request turn and the service's person in a service turn.
  - `INhAssistantConversations` (`CreateAsync`, `GetAsync`, `CancelAsync`) and
    `INhAssistantConversationTurns.AnswerAsync` (CS6) for a person the application authenticated, with
    the endpoints' checks and codes.
- **(b) Only the facts and `AnswerAsync`.** The application keeps the endpoints for creating, reading and
  cancelling, and it keeps finding the person through its own route.
- **(c) Leave it.**

#### FB6. A question's status and notice (CS6)

A turn that waits for a question keeps the conversation and turn status `waiting-for-approval`, with
the question in `pendingQuestion`. The Web Push notifier then tells the person whose turn it is, with an
approval text, even when the question is addressed to someone else.

- **(a) Keep the status, fix the notice (advice).** The status stays, so cancel, the busy refusal, the
  stale checks and existing clients keep working. The notifier sends a question notice of its own to the
  addressee. `AnswerAsync` comes with FB5.
- **(b) A status of its own, `waiting-for-answer`.** Clearer, but breaking for every client and filter
  that knows the statuses, and the composer already shows `waiting-for-answer` from `pendingQuestion`.

#### FB7. Questions and approvals that pass their lifetime (CS9)

A question expires only when someone answers it, and a chat approval only when someone decides it. Until
then the chat refuses new messages as busy, unless someone stops the turn.

- **(a) A new message closes what expired (advice).** A message in a chat whose waiting question or
  approval passed its lifetime closes it as expired, exactly as a late answer or decision does
  (`assistant-question-expired`, `assistant-approval-expired`, audited), and then starts its turn. No
  job is needed, and a read stays free of writes.
- **(b) A maintenance sweep.** The run maintenance expires them on time and publishes the change. Exact,
  but it needs a maintenance host for chats too and writes for conversations nobody opens.
- **(c) Leave it.**

#### FB8. Chat settings without run settings

`SkillsInChat`, `AllowQuestions` and `MaxConsultationsPerTurn` are on `NhAssistantAgentRunBuilder`, so
a chat-only agent gets run settings from `ConfigureAgent`. It must then say what it works on to stay off
every object's page, it must switch files on explicitly, and it can be started as a run. Its skills
live there too (`Skill`), so a separate chat builder would repeat them.

- **(a) `run.ChatOnly()` (advice).** The agent keeps its run settings for chat (skills, questions,
  consultations, token ceilings, files), but it works on no subject:
  - It is on no object's page, and it cannot be started, triggered, handed off to or replayed.
  - The host refuses to start when `ChatOnly` is combined with `WorksOn`, triggers, hand-offs, shadow,
    `HideFromChat` or a consultation of the agent.
- **(b) A chat builder.** `ConfigureChat(agentId, chat => ...)` with the chat settings and its own
  `Skill`. That gives two ways to define a skill, and two places to check.
- **(c) Leave it.**

### D. Agents, MCP and packages

#### FB9. An editor that drops an MCP assignment (MP2)

An assignment writes only its own row (MP2). But the agent editor's save sends the whole
`mcpServerIds` list, and the endpoint reads an omitted list as empty (`body.McpServerIds ?? []`). So an
editor opened before an assignment drops it on save, and a client that omits the list clears every
assignment.

- **(a) An omitted list keeps the assignments; the editor assigns one by one (advice).**
  - `PUT admin/agents/{id}` keeps the assignments when `mcpServerIds` is omitted, as it does for the
    other fields.
  - New endpoints `PUT` and `DELETE admin/mcp-servers/{id}/agents/{agentId}` wrap
    `INhAssistantMcpServers.AssignAsync` and `UnassignAsync`.
  - The editor sends no list and assigns or unassigns only the servers the person changed.
  - The omitted list gets a breaking row: a client that cleared the assignments that way sends `[]`.
- **(b) The list the editor loaded.** The request carries the list it started from, and the server
  applies only the difference. It needs no new endpoints, but it adds a second list to a contract that
  already has one.
- **(c) Leave it.**

#### FB10. Reviewed tools of a server that cannot be reached yet

`INhAssistantMcpServers` creates tool rows only from a sync. A server that the host cannot reach yet,
such as one that a move brings along, gets no tools, and the administrator who syncs it later sets every
reviewed effect and description again: each new tool starts as a mutation.

- **(a) Settings for tools that no sync has seen yet (advice).**
  - `UpdateToolAsync` and `PUT admin/mcp-servers/{id}/tools/{remoteName}` also take a tool that no sync
    has seen: they store its reviewed settings as a row with the existing status `missing` and no
    schema hash, so it is never offered.
  - The first sync that sees the tool makes it `available` with those settings. A later change of its
    schema follows the existing `schema-changed` rule.
  - A row that no sync has seen yet is listed as missing, so the administrator sees what is expected.
- **(b) Tools on the create request.** `NhAssistantMcpServerRequest.Tools` with the same settings. That
  is one call for a whole server, but a second way to set a tool.
- **(c) Leave it.**

#### FB11. Promote a variant as the package's next version

`PromoteShadowVariantAsync` (LS31) changes the live agent through the update path. A field that the live
agent's package locks is refused, the live agent keeps its run settings and metadata, and a variant that
its own package links cannot be deleted (`assistant-package-locked`). A new version of a package that
waits as a shadow variant is therefore still promoted by importing the package onto the live agent
again.

- **(a) A promotion that takes the variant's package version (advice).**
  - When every part of the variant is linked to a package, promote with `AsPackageVersion` applies the
    variant's parts to the live agent with the import's own rules: the new version's locks and hashes,
    run settings, and the metadata entries that the package carries.
  - It relinks the live agent to that package version, then deletes the variant and its links.
  - One audit event; a part that the import would refuse refuses the promotion.
- **(b) Document the import as the promotion.** The sample and the guidance show "evaluate as a variant,
  import onto the live agent, delete the variant", and deleting a variant unlinks it in the same step.
  That is less code, but still three steps for the administrator.
- **(c) Only copy run settings and metadata on promotion.** Locks still refuse, so packages with locks
  stay on the import path.

### E. Background operations

#### FB12. A handler error outside a step during a cancellation (LB8)

LB1 lets a durable cancellation that the failed-step write finds win over the step's error. A handler
error that is thrown outside a step while the operation's cancellation is requested is still recorded
as `handler-failed` and may retry.

- **(a) The same durable check on the runner's failure path (advice).** A handler error while the
  attempt's cancellation is requested is recorded as `Cancelled`, the error is logged as a warning, and
  nothing retries. A fence or contention signal still wins, as in LB1. The tests run on both providers.
- **(b) Leave it.**

#### FB13. The background-operations hub path (LC12)

The `HubPath` validation of background operations still requires a path under `/hub`. Since LS27 the
query token follows the configured hub paths, so the rule no longer protects anything.

- **(a) Any application path, as the assistant's hub (advice).** The path must be absolute and must not
  lie under the API prefixes. The default stays `/hub/background-operations`.
- **(b) Leave it.**

### F. Delivery

#### FB14. What waits for the application's evidence

- **(a) Nothing (advice).** Every choice has enough evidence; FB1 measures before and after in its lane.
- **(b) Ask the application for its CPU sample of FB1 first.**

#### FB15. Trains

- **(a) Two trains, in this order (advice).**
  - **Train 24, host start and background operations:** FB1, FB12, FB13. FB1 touches many endpoint
    files of the assistant, so it lands before the features change them.
  - **Train 25, assistant features:** FB2 to FB11, in four lanes:
    - runs and decisions: FB2, FB3, FB4, which carries the migration;
    - chat: FB5 to FB7;
    - agents and packages: FB8, FB11;
    - MCP: FB9, FB10.
- **(b) One train** with five lanes. FB1 then conflicts with every assistant lane.

## Lane choices

| # | Choice |
|---|---|
| RG1 | Built by lane RG (2026-10-26): **the measurement is an opt-in test, not a benchmark project**: `AssistantRequestDelegateTests.Measure_mapping_and_building_the_assistant_endpoints` runs only with `NH_MEASURE_ENDPOINTS=1` and reports, for 20 hosts in one process after three warm-up hosts and for the first host, the wall-clock and process CPU time of `MapNewHeapAssistant` plus the first read of every endpoint, and how many endpoints run generated delegates; run alone in Release it measured 225–231 ms wall-clock (median), 248–270 ms CPU (mean) and 270 ms for the first host before, and 6.5–7.0 ms, 6–11 ms and 91–95 ms after (Windows' 15.6 ms CPU tick makes the mean the comparable CPU figure), for Lars to review. |
| RG2 | Built by lane RG (2026-10-26): **"generated" is read from each endpoint's own request delegate**: the test passes when every route handler under the prefix runs a `RequestDelegate` declared in `Microsoft.AspNetCore.Http.Generated` inside the assistant's assembly, where the generator writes it; a factory delegate is declared in ASP.NET Core or is a dynamic method. Built with `-p:EnableRequestDelegateGenerator=false` the test fails and names all 139 endpoints. The generator's `GeneratedCodeAttribute` metadata would be a second signal and is not asserted, for Lars to review. |
| RG3 | Built by lane RG (2026-10-26): **no handler stays on the factory and no handler changed**: the generator took all 139 endpoints without an RDG diagnostic (every handler is a method group with route, query, `HttpContext` and service parameters; the handlers read bodies and forms themselves). The SDK adds the interceptors namespace itself for a `Microsoft.NET.Sdk` project with the ASP.NET Core framework reference, so the project sets only `EnableRequestDelegateGenerator`, for Lars to review. |
| RG4 | Built by lane RG (2026-10-26): **parity is proven against the factory and recorded as no breaking change**: a one-time diff of every endpoint's metadata in a minimal host, factory against generator, differs only in the generator's `GeneratedCodeAttribute` and in `IParameterBindingMetadata.HasTryParse`, which the generator reports false for `string` parameters; the sample's `/openapi/v1.json` (205 paths) is identical apart from the server address; `Malformed_query_values_answer_400_before_the_handler_and_404_when_the_assistant_is_off` passes with both. The release note names the two metadata differences in its text, not as a breaking row, for Lars to review. |
| RG5 | Built by lane RG (2026-10-26): **the binding test runs as production does**: it sets `RouteHandlerOptions.ThrowOnBadRequest = false` (the test host runs as Development, where factory and generator both throw `BadHttpRequestException`) and switches the assistant off with `AssistantSwitch` in the same host, so the class starts one database-backed host; malformed `int?`, `DateTimeOffset?`, `bool?` and `Guid?` query values answer 400 without a body, well-formed ones reach the handler and a disabled assistant answers 404. The generation test and the measurement build hosts without starting them and need no database, for Lars to review. |
| RG6 | Built by lane RG (2026-10-26): **no other library enables the generator**: measured with the same method (warm median per host, factory and then generator, Release): media 11 endpoints 16.1 and 2.1 ms (its upload endpoint stays on the factory, RDG003); the proxy API 11 endpoints 7.1 and 0.4 ms; authentication (password, refresh and two-factor) 21 endpoints 8.5 ms, which the generator cannot take because `IAuthenticationEndpoint.Handler` is a `Delegate` value (RDG002); background operations map controllers and a hub, no minimal APIs; JWKS is one endpoint. None is comparable to the assistant's 225–231 ms, for Lars to review. |
| RG7 | Built by lane RG (2026-10-26): **one sentence in `nh-fast-consumer-tests` and no sample case**: the rule's host paragraph says that mapping the assistant adds a few milliseconds to a host start; the consumer guide and the skill references were regenerated. No sample case is added or changed: the change is build-time only and not observable, which AGENTS.md exempts from a new case, and the sample application and test projects keep the generator off, for Lars to review. |
| RG8 | Built by lane RG (2026-10-26): **no trimming or native AOT claim**: the release note claims only the host-start cost; no trimming or AOT analyzer run was made for the library, for Lars to review. |
| BO1 | Built by lane BO (2026-10-26): **the runner's failure path lets the three stop signals of LB1 win over an error that the handler throws outside a step**: a stop the heartbeat recorded (durable cancellation, contention, lost ownership) ends the attempt as it ends a cancelled execution, then a write that found its fence lost records nothing, then the fenced durable check (`IsCancellationRequestedAsync`) decides; a heartbeat failure keeps `handler-failed`. Heartbeat contention therefore also wins without a cancellation request (rescheduled as `concurrency-busy`, no retry spent), for Lars to review. |
| BO2 | Built by lane BO (2026-10-26): **a cancellation that the durable check finds after the soft time-out fired ends `TimedOut` (`soft-timeout`)**, as the cancelled execution orders it (LS5, LB6), so the request is not lost to a retry; a cancellation the heartbeat recorded first ends `Cancelled`, and an error after a fired time-out without a request stays `handler-failed` with its retries, for Lars to review. |
| BO3 | Built by lane BO (2026-10-26): **the handler's error is logged as a warning with the operation, the attempt and the stop signal, without a diagnostic correlation id**, because the operation stores `cancelled` and nothing for an operator to correlate; a lost fence that the attempt's own write found is logged as the lost-ownership warning instead of an error, for Lars to review. |
| BO4 | Built by lane BO (2026-10-26): **`HubPath` must be an absolute application path below the root and outside `/background-operations`**: the existing rule (no relative, empty, protocol-relative path, query, fragment or backslash), the root, because it names no hub and the query token is never read for it, and the operations' own controller route, an internal constant `NhBackgroundOperationsOptions.ApiRoute` that a test compares with both controllers' route attributes, which keep their literals so the public declarations stay unchanged; an application's own API prefix is unknown to the library and not checked, and `OperationUrlPrefix` is a client link prefix, not an API route, for Lars to review. |
| BO5 | Built by lane BO (2026-10-26): **the live connection is proved by a TestServer host with the hub at `/realtime/operations` and a WebSocket that speaks the JSON hub protocol itself**, so `NewHeap.Platform.AspNet.Common.Tests` takes no SignalR client package; the host's startup check reads an in-memory EF context because the test stores no operation, and the platform's own bearer setup is tested with the module registered at that path, for Lars to review. |
| BO6 | Built by lane BO (2026-10-26): **SPM-325 is extended, not SPM-338**: the project-analysis child stores its analysis in `ProjectAnalysisArchive`, the sample's stand-in for another system whose client reports a cancelled call as an error of its own, outside a step; `Program.cs` registers it without latency, and the sample test gives it a long latency and heartbeats 200 ms apart and asserts `Cancelled` with a single attempt under the child's default three retries, for Lars to review. |
| BO7 | Built by lane BO (2026-10-26): **FB13 has no sample change of the path**: the sample keeps `/hub/background-operations`, which its Angular `hubUrlSuffix` follows, documents `HubPath` next to `OperationUrlPrefix` in `Program.cs` and extends SPM-083 with the library tests as evidence, for Lars to review. |
| BO8 | Built by lane BO (2026-10-26): **one release-note section for `NewHeap.Platform.AspNet.Common` with three rows**: the cancellation instead of `handler-failed` (monitoring or tests that expected `handler-failed` read `Cancelled`), heartbeat contention without a spent retry, and the relaxed `HubPath` as no breaking change, for Lars to review. |
| CP1 | Built by lane CP (2026-10-26): **`ChatOnly()` makes what the agent works on a declared "nothing" and records every run-only builder call by name**, so the one works-on rule refuses every start path with the existing `assistant-agent-does-not-work-on` and startup refuses a run-only call before or after `ChatOnly()` with one message that names them; no new error code, for Lars to review. |
| CP2 | Built by lane CP (2026-10-26): **refused with `ChatOnly()`: `WorksOn`, `WorksOnAllObjects`, `WorksOnApplication`, `PerDivision`, `RunPolicy`, `ViewPolicy`, `InGroup`, `AcceptHandOffs`, `AllowConsultation`, `ConsultableAsData`, `Shadow`, `ShadowOf`, `HideFromChat`, `StartsOff`, `Enabled`, `Accountability`, `ServiceAccount`, `Grant` (with `TriggerOnly`), `DelegateFromInitiator`, `ActWithAccountableRights`, `ElevatedReads`, `AllowGatewayReads`, `Retry`, `Budget`, `Triage`, `Memory` and `UseRuleHandler`, and a trigger in code that starts it, another agent's `ShadowOf` or hand-off source, and `UseHandOffsAsData` or `UseShadowVariantsAsData` naming it**; kept: `Skill` (its own settings unchanged), `SkillsInChat`, `AllowQuestions`, `MaxConsultationsPerTurn`, `MaxTokensPerRun`, `AcceptsFiles`, `DataClassification`, `UseApprovalPolicy` (a chat's approvals use it) and `Metadata`; grants, accountability and the read settings act in thread turns only, which a chat-only agent never has, for Lars to review. |
| CP3 | Built by lane CP (2026-10-26): **at runtime a data trigger (on save, and out of effect on load), the agent's shadow switch and a board refuse a chat-only agent with `assistant-agent-does-not-work-on`, a replay from it is `assistant-message-invalid` and into it `assistant-replay-not-shadow`, the subject work leaves it out also when asked for by id, run maintenance treats a host with only chat-only agents as one without runs, and startup needs background operations only for agents that run**; its run state changes and its skills' states and shadow modes stay possible (a skill's state decides `use_skill` in a chat), and the administration hides the agent's run state and shadow actions, "Run now" and runs, for Lars to review. |
| CP4 | Built by lane CP (2026-10-26): **`ChatOnly` is code only and is exposed as `ChatOnly` of `NhAssistantAgentRunOptions`, the catalog's `NhAssistantAgent`, `NhAssistantAdminAgentDto`, `NhAssistantCodeRunSettingsDto` and `NhAssistantAgentInEffectDto`**; an agent created in the administration gets no chat-only switch, since one without run settings already only chats; skills of a chat-only agent may still be data, for Lars to review. |
| CP5 | Built by lane CP (2026-10-26): **the promotion as package version is the import's own plan and transaction, given a promotion context**: the variant's parts, read as an export reads them, become a package of the live agent under the variant's package id and version; the live agent is relinked as a whole (no part stays local), its links to another package do not refuse it, the variant's trigger ids count as the live agent's, the host check leaves the variant out, and the write deletes the variant at its version, with its links by cascade, before it writes the parts, for Lars to review. |
| CP6 | Built by lane CP (2026-10-26): **the live agent keeps its display name, chat offer and technical account (it takes the variant's account only when it has none), and an agent in code keeps its run settings in code**: the variant's run settings are left out of the overlay, as an overlay never carries them (PK2), and are not linked; instructions from an asset onto an agent in code refuse with `instructionsAsset: not-allowed`, as its update does; groups do not travel, for Lars to review. |
| CP7 | Built by lane CP (2026-10-26): **the live agent leaves the package it belonged to before as an agent left behind does (its links end, it leaves that package's agents, the row keeps its version), and the variant's package row keeps its version, hash and import and holds the live agent instead of the variant**, while the package's other agents stay linked; a local change of the variant is promoted as it is, and the new links take the hashes after the promotion, as an import records them, for Lars to review. |
| CP8 | Built by lane CP (2026-10-26): **one `AdminAgentShadowVariantPromoted` (kind 91) with the result `promoted-as-package-version` (`NhAssistantShadowVariantPromotions`) and the package's id, version and hash**, after the parts' own change events as an import records them, and no `AdminAgentPackageImported`, `AdminAgentPackageUnlinked` or `AdminAgentDeleted` for it; the kinds 110 to 112 stay unused, for Lars to review. |
| CP9 | Built by lane CP (2026-10-26): **every part the variant carries must be linked to one package, otherwise `assistant-package-not-linked` (404, its existing status), and a part the import refuses refuses everything with `assistant-package-refused`, `parts: invalid` and `parts.{part}.{field}` per refused part** (`parts.{part}: invalid` for one refused with a code), because a promotion has no preview that says why; `MoveSkills` with it is `moveSkills: not-allowed`, and without the option a package-linked variant stays `assistant-package-locked`, for Lars to review. |
| CP10 | Built by lane CP (2026-10-26): **the outcome is `NhAssistantShadowVariantPromotionDto.Package` (`NhAssistantAgentPackageImportDto`, every part's outcome) with `MovedSkills` the skills the version linked, the HTTP answer's `package` (left out when null), and `NhAssistantShadowVariantDto.PackageId` and `PackageVersion`** so the administration offers the option for a linked variant; whether every part is linked stays the server's check, for Lars to review. |
| CP11 | Built by lane CP (2026-10-26): **the agents page marks a chat-only agent with a badge and a hint instead of what it works on and keeps its run configuration; the promotion's confirmation offers "As the package's next version" for a linked variant, which hides the move-skills option and the update path's explanation**; the mock API promotes through its own import with the same rules, names a variant's package and takes `chatOnly` on a code agent of a scenario, for Lars to review. |
| CP12 | Built by lane CP (2026-10-26): **SPM-343 is a new chat helper (`SampleChatHelperComposition`) with a meeting notes skill in chat, questions and a consultation limit; SPM-344 extends SPM-321's desks with the kickoff desk's next version as a variant in the package `sample-project-desks-next` (`SampleAgentPackagesComposition.NextKickoffVersion`), promoted as the package's version**; both run on the sample's PostgreSQL host, the library tests on both providers, for Lars to review. |
