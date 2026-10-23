# Consultations in the Administration Plan

This document works out how administrators configure consultations between agents as data, the way
skills, triggers and run settings already are: which agent may be consulted about which subject type,
with which description and instructions, read-only or with proposals, and by which routes, within a
ceiling that code sets. It follows Lars's decision CP25 (b) of the
[consultation proposals plan](ai-consultation-proposals-plan.md) and builds on the
[agent consultation plan](ai-agent-consultation-plan.md) (C1 to C8), the
[run configuration as data plan](ai-agent-run-configuration-plan.md) (D1 to D8), its
[UX plan](ai-agent-run-configuration-ux-plan.md), the [runtime overrides](ai-agent-runtime-overrides-plan.md)
and the [trigger event filters](ai-trigger-event-filters-plan.md). It was checked against `main` at
`b14452a0` on 2026-10-23. It is a plan, not usage documentation.

## Status

Proposed on 2026-10-23. Lars decided CP25 (b): consultations become configurable in the
administration. Lars decided AC1 to AC24 on 2026-10-23, as recorded below: the recommendation everywhere except AC8 (b), AC13 (b) and AC15 (b).

P1 (library, migrations, sample SPM-311, guidance and release notes) was built on 2026-10-23 on
`feature/admin-consultations`, on top of the AC1 fix; see [Implementation of P1](#implementation-of-p1).
The lane's own choices are AC25 to AC34. P2 (administration UI and mock) was built on 2026-10-23 on
`feature/admin-consultations-ui`; see [Implementation of P2](#implementation-of-p2). Its lane's choices
are AC35 to AC45. Lars decided AC25 to AC45 on 2026-10-23: the recommendation everywhere except AC34
(b), with additions to AC26 (an MCP test over HTTP) and AC45 (the release notes); those three are not
built yet. The browser walk-through of the portal and the playground is open.

## Implementation of P1

- **Ceilings.** `UseConsultationsAsData` and `ConsultableAsData` take an `NhAssistantConsultationCeilingBuilder`
  (`AllowRoutes`, `AllowRequests`, `AllowMcp`, `MaxLimits`); `NhAssistantRegistrationState.ConsultationCeilingOf`
  gives an agent's ceiling (the application's for an agent created in the administration, both together
  for a consultable agent in code, none otherwise). `ValidateAgentRuns` refuses a code ceiling without
  `UseConsultationsAsData`, beyond it, or with `subject-agent-rights` for an agent that answers to the
  subject's responsible person or for itself without grants.
- **Storage.** `AssistantAgentConsultationDefinition` (key agent and subject type, `RoutesJson`, limits,
  `ExposeToMcp`, version and concurrency stamp, cascade with its agent) and
  `AssistantConsultation.ConsultationVersion`, migrations `AgentConsultationDefinitions` for SQL Server and
  PostgreSQL. Every save and removal changes the run configuration stamp in its transaction; run settings
  stay while consultations belong to the agent.
- **Snapshot.** `NhAssistantRunConfigurationValidator.CheckConsultation` checks a row on save and on load
  (AC11) and builds its `NhAssistantConsultationOptions` with `Source` `data` and `DataVersion`;
  `NhAssistantRunConfigurations.Build` adds them with `WithDataConsultations` after run settings and skills
  and lists problems of kind `consultation`, one per part that no longer fits.
- **Tools.** The code catalog reads `CodeAgentRuns`. `NhAssistantDataConsultationToolCatalog`
  (`assistant-consultations-data`) builds an attested view per snapshot, binds functions late through
  `NhAssistantConsultationToolHandler.ConsultDataAsync` and `RequestDataAsync` with a contract check, and
  raises `Changed` when its listing changes. It implements the new `INhAiChangingToolCatalog` of
  NewHeap.Platform.AI.Common: discovery refreshes such catalogs, the agent framework adapter and the MCP
  export read one view, the MCP export attests every view once and skips a failing view or a taken export
  name, and `WithNewHeapPlatformAITools` signals `notifications/tools/list_changed` through the MCP SDK's
  tool collection when a changing catalog may reach MCP clients. The AC1 seam
  (`NhAssistantConsultationPersonRightsTools`) covers the data tools by their prefixes.
- **Administration.** `INhAssistantAgentConfiguration.SaveConsultationAsync` and `RemoveConsultationAsync`,
  `PUT` and `DELETE admin/agents/{id}/consultations/{subjectType}`, `CodeConsultations`, `DataConsultations`
  and `ConsultationCeiling` on the agent configuration, `ConsultationCeiling` on the run catalog, codes
  `assistant-consultation-in-code` (409), `assistant-consultation-not-found` (404),
  `assistant-consultations-not-allowed` (409), audit `AdminAgentConsultationDefinitionChanged` (66) and
  `ConsultationSource` and `ConsultationVersion` on the audit event.
- **Freshness.** Discovery (chat and agent turns, MCP listing), `GET admin/tools`, the route switches and the
  administration read the stamp at most once per check interval; the background refresh runs on every
  assistant host when consultations are data.
- **What P2 needs.** The endpoints and DTOs above; the new error codes and the field error `not-allowed` in
  the Angular error and field texts; the problem codes of kind `consultation`, including the lane's
  `requests-not-allowed` and `mcp-not-allowed` (AC27); `AskingAgents`, `RoutesInEffect` and `Problems` on
  each data consultation for the section and the flow view.

## Implementation of P2

- **Models and API.** `@newheap/platform-ai-chat` has `CodeConsultation`, `DataConsultation`,
  `DataConsultationRequest`, `ConsultationCeiling`, `ConsultationSubjectRole` and `ConsultationExposure`;
  `AgentRunConfiguration` carries `codeConsultations`, `dataConsultations` and `consultationCeiling`,
  `RunCatalog` the application's `consultationCeiling`, and `RunConfigurationProblemKind` the kind
  `consultation`. `NhAssistantAdminApiService.saveDataConsultation` and `removeDataConsultation` call the
  P1 endpoints. The three new codes are in `NH_ASSISTANT_SERVER_ERROR_CODES` and the mock's status table,
  with English and Dutch texts, as are the field error `not-allowed` and every problem code of kind
  `consultation`.
- **Section and form (AC20).** `NhAssistantAgentRunConfigurationComponent` has a Consultations section
  after the triggers: each consultation by its subject type's name, in code read only, as data with its
  version, who may ask and who decides in a sentence, the asking agents (AC5) with a link to their
  editors (`openAgent`), its tool id, limits and problems, and edit and delete. The form
  (`nh-assistant-consultation-form`, internal) follows the trigger and skill forms. The CP21 switches
  moved into the section for every agent with consultations and read the routes again after every
  change. Without a ceiling the section says why (AC35).
- **Flow view.** The when step lists the consultations with what they may do and how many agents ask
  them, an entry to add one and to edit each one defined as data; problems of kind `consultation` show
  at that step with the form that fixes them.
- **Mock API.** `NhAssistantMockAdminScenario.consultationsAsData`, `NhAssistantMockAdminAgent.consultableAsData`,
  `consultationDescriptions` and `dataConsultations`; `nh-assistant-mock-consultations.ts` saves and
  removes within the ceilings with the server's codes and field errors, checks every read again
  (problems, routes in effect, capped parts), lists the tools in `GET admin/tools` and counts the
  consultations for the route switches.
- **Sample.** SPM-311 also names the administration: the management playground's mock scenario has the
  project starter with its consultation about projects as the API's seeder defines it, and the project
  assistant selects its tool; the portal's administration shows the same through the library.
- **Tests.** `nh-assistant-consultations-admin.spec.ts` (mock contract, section, form, flow), the admin API
  service, mock status, translations and consultation routes specs.

## Problem

- Consultations exist only in code: `ConfigureAgent(...).AllowConsultation(subjectType, ...)` adds an
  `NhAssistantConsultationOptions` to the agent's code run options. Changing a description, the
  consultation instructions or the limits needs a developer and a deploy.
- Agents created in the administration have no consultations at all. The run configuration as data
  listed "hand-offs and consultations of administration agents" as out of scope, CP25 (a) kept it so,
  and the route switches of CP21 show only for agents in code.
- The two keys of a consultation are asymmetric. The asking side is already data: an administration
  agent's tool selectors are data, and an administrator can override the tool selectors of an agent in
  code (`IsOverridden`). The consulted side, which generates the tool, is code only.

## Goals

- Administrators add, change and remove consultations of an agent per subject type, for agents
  created in the administration and, where code allows it, for agents in code.
- Data never widens rights beyond code: reads stay the asker's (C2), proposals follow the routes of
  CP1 to CP34, and every route, limit and exposure stays within a ceiling that code registers.
- The same pattern as the run configuration as data: stored rows with versions and concurrency stamps,
  one stamp that reloads every host, validation against code building blocks on save and on load,
  problems instead of startup failures, the admin service and endpoints, the administration screens,
  audit.
- The generated consult and request tools of data consultations appear and disappear without a
  restart, and the attested catalog of code consultations and the MCP export stay as they are.

## Non-goals

- Grants as data, MCP exposure as data (AC8), the consultation depth as data, and changing a
  consultation registered in code (its description, instructions, limits or routes) as data (AC17). The
  CP21 switches stay the only runtime change of a code consultation.
- Hand-offs (`AcceptHandOffs`) as data.
- Shadow evaluation of consultations (an agent in shadow is never consulted, D4 of the runtime
  overrides).
- A generic consult tool with an agent argument.

## How consultations work today

Read from `NhAssistantAgentConsultation.cs`, `NhAssistantAgentRuns.cs`, `NhAssistantBuilder.cs`,
`NhAssistantConsultationTools.cs`, `NhAssistantConsultations.cs`, `NhAssistantConsultationProposals.cs`,
`NhAssistantTurnRunner.cs`, `NhAssistantAgentControl.cs`, `NhAiAgentInvocation.cs`,
`NhAiToolDiscovery.cs`, `NhAiMcpToolAdapter.cs` and `NhAiAspNetMcpIntegration.cs`.

### Registration

- `AllowConsultation(subjectType, consult => ...)` creates one `NhAssistantConsultationOptions` per agent
  and subject type (a second one for the same type throws). It holds: `Description` (at most 1,000
  characters), `Policies` (authorization policies the asker must pass), `MinimumRole` (default
  `Reader`, never `None`), the proposal settings `ProposalRole` (default `Participant`), `AskerDecides`
  (A), `AtSubjectRights` (`Asker`, today's route and the default, or `Agent`, B, or none) and
  `ProposalPolicyId` (CP5), the limits `MaxToolCalls` (0 to 32, default 4), `Timeout` (1 second to 10
  minutes, default 60 seconds), `MaxAnswerChars` (200 to 32,000, default 4,000) and
  `MaxHistoryMessages` (0 to 200, default 20), `AllowRequests`, `Exposure` (default `Local | Agent`,
  `Mcp` only on request), `Instructions` (an `NhAiTextAsset`) and `DataClassification` (default
  `Internal`).
- `AllowedRoutes` derives the routes the registration allows, the ceiling of CP21.

### Startup checks

- `NhAssistantRegistrationState.ValidateAgentRuns`: the agent works on the subject type (`WorksOn`);
  the consultation's proposal policy is registered; a consultation with `AtSubject(Agent)` is refused
  for an agent that answers to the subject's responsible person, for `nh-system` and for a known
  policy whose approvers are only the initiator.
- `ValidateConsultationsAsync` at host start: the subject type has an access resolver, every policy of
  the generated descriptors is registered, and the consultation catalog passes
  `NhAiToolCatalogAttestation.Validate`.

### The generated tools

- `NhAssistantConsultationToolCatalog` (catalog `assistant-consultations`) is an
  `INhAiAttestedToolCatalog` singleton. It builds its model **once**, lazily, from
  `registration.AgentRuns.Values.SelectMany(run => run.Consultations)`.
- Per consultation it creates `assistant.consult.{agentId}.{subjectType}` (`ReadOnly`, policy-controlled
  approval, timeout plus 15 seconds) and, with `AllowRequests`, `assistant.request.{agentId}.{subjectType}`
  (`IdempotentMutation`, no approval, required idempotency). The export name is
  `consult_{agentId}_{subjectType}_v1`, bounded to 64 characters. The descriptor's authorization
  policies are the consultation's `Policies`, else the agent's `RequiredPolicy`, else the assistant's
  access policy.
- The contract hash covers the id, the effect, the input and output schemas and the policies; the
  description (with the registration's description and the routes text) is not part of it. The
  catalog's attestation hash is the hash of `id@version:contractHash` lines.
- Every function is a governed function bound to its descriptor and runs through the shared invoker.
  The function captures the `NhAssistantConsultationOptions` instance it was built from.
- Consumers read the catalogs live: tool discovery (`NhAiToolDiscoveryService`), the turn runner's
  `SelectToolsAsync`, the admin tool list (`GET admin/tools`) and the MCP adapter, which per request
  reads `Descriptors`, then `CreateFunctions`, and throws when their counts differ. MCP export names are
  checked for uniqueness once, at startup (`NhAiMcpAuthorityStartupValidator`). Nothing sends MCP
  clients a tool list change.

### Who may consult whom

- Two keys (C4): the consulted agent allows the consultation in code; the asking agent selects the
  generated tool id in its tool selectors. An agent never consults itself.
- The asker passes the descriptor's policies through the invoker and the gate, and has the
  consultation's `MinimumRole` on the subject, resolved live; a missing subject and missing access
  answer the same code.
- The consult and request tools never allow an agent grant (`AllowAgentGrant = false`), so they run
  with a person's rights: the person in the request, or the initiator who delegated an agent turn.

### A consultation

- `NhAssistantConsultationService.PrepareAsync` refuses a chain deeper than
  `NhAssistantLimits.MaxConsultationDepth` (default 1, at most 3) or one that already contains the
  agent; a turn without time left; an agent without run options, disabled or in shadow; a subject type
  the agent does not work on; a subject the asker may not read; the asker's daily cap
  (`MaxConsultationsPerActorPerDay`, 50); and the agent's spent daily budget. It finds or creates the
  agent's subject thread, records an `AssistantConsultation` and runs one turn.
- The consult turn's scope has no grants, delegates from the asker, records proposals only
  (`RequireApprovalForEffects`), and offers further consult tools only below the depth limit and never
  for an agent in the chain. The consulted agent reads the platform's consultation instruction with the
  routes text (CP33), then the registration's `Instructions`.
- Proposals follow CP3: `NhAssistantConsultationRouting.Of(options, agent.ConsultationRoutes)` applies
  the CP21 switches (they can switch a route off, never on beyond the registration); A when it applies,
  else the route at the subject, else refused. B checks the agent's accountability, `nh-system`, the
  policy's approvers, the agent's own rights through `INhAiAgentToolAuthorizer` (grants or the
  technical account's rights) and the open cap per asker.
- `NhAssistantConsultationProposals.OptionsOf` finds a consultation's registration again by agent and
  subject type in `registration.AgentRuns`, for the "leave for approvers" route of an A proposal.

### The CP21 switches

`INhAssistantAgentControl.SetConsultationRoutesAsync` stores three nullable switches per agent on
`AssistantAgent` (migration `AgentConsultationRoutes`). The ceiling is the union of the agent's
registrations' `AllowedRoutes` in `registration.AgentRuns`; a switch to `on` outside it fails with
`assistant-consult-route-not-allowed`. The Angular section shows only for agents in code.

### Audit

`AgentConsulted` per consultation (content-free); inner calls and approval events carry the
consultation id, depth, asking agent and route; `AdminAgentConsultationRoutesChanged` (65) for a switch.

### A finding: the accountable path

Reading the gate and the handler shows a path the consultation plan did not intend. In a headless turn
of an agent that acts with its accountable account's rights (`ActWithAccountableRights`, always on for
an agent created in the administration with run settings) and that has an initiator but does not
delegate from it, `NhAiAgentInvocationGate` allows a tool when the technical account passes the
tool's policies, without looking at `AllowAgentGrant`. The consult tool's policies are typically the
access policy, which a technical account passes. `NhAssistantConsultationToolHandler.ResolveAsker`
then takes the scope's initiator as the asker, whatever the grant source, and the consult turn rebuilds
that person's principal. A trigger whose event names an initiator (for example the creator of a
project) can thus make such an agent consult another agent, about any subject that person may read,
with that person's rights, and write the answer into its own thread, which other people read. Agents created in the
administration can already select consult tools today. A focused test confirmed the path, and it was
closed in the same train (AC1, `AssistantConsultationDelegationTests`).

## How run configuration became data

Consultations as data follow what skills, triggers and run settings do:

| Concern | Skills, triggers and run settings today |
|---|---|
| Storage | `AssistantAgentSkillDefinition` (versions: live, candidate, superseded), `AssistantAgentTriggerDefinition` (one row, `Version`, `ConcurrencyStamp`), `AssistantAgentRunConfiguration` (one row per administration agent, `Version`). |
| Reload | Every change also changes `AssistantRunConfigurationStamp` in the same transaction (`ChangeStampAsync`). `NhAssistantRunConfigurations.EnsureCurrentAsync` reads the stamp at most once per second before every fire, run start and admin read; a background refresh checks every 30 seconds on hosts that run agents. |
| Effective configuration | `NhAssistantRunConfigurations.Build` merges code and data into an immutable `NhAssistantRunSnapshot`; `registration.AgentRuns` returns it, so readers stay synchronous. Data skills join through `WithDataSkills`, a copy of the code options. |
| Validation | `NhAssistantRunConfigurationValidator` checks data against what code registered, on save and on every load. |
| Problems | Data that no longer fits is left out and listed as `NhAssistantRunConfigurationProblem` (agent, kind, id, code), logged and shown in the administration; startup never fails on it. `unknown-filter` is the one problem that keeps its trigger, failing closed. |
| Service and API | `INhAssistantAgentConfiguration` (also for package imports) and `admin/agents/{id}/...` endpoints under the admin policy, with field errors and `assistant-version-conflict`. |
| Code wins | An id declared in code cannot be data (`conflicts-with-code`); code skills and triggers cannot change. |
| Start state | New skills and triggers start in shadow. |
| Audit | `AdminAgentSkillDefinitionChanged`, `AdminAgentTriggerDefinitionChanged`, `AdminAgentRunSettingsChanged`, with version and result code, never content. |
| UI | `NhAssistantAgentRunConfigurationComponent`: the flow view (U6) with problems at their step, run settings, skills with candidates, triggers, the CP21 routes section. |

## Design

### The ceiling in code (AC2, AC3, AC4)

```csharp
services.AddNewHeapAssistant(assistant => assistant
    // The application lets administrators configure consultations, within this ceiling. Without it
    // consultations stay code only.
    .UseConsultationsAsData(data => data
        .AllowRoutes(
            NhAssistantConsultationRoutes.AskerDecides,         // A
            NhAssistantConsultationRoutes.SubjectAgentRights)    // B; today's route is not named here
        .AllowRequests()
        .MaxLimits(maxToolCalls: 4, timeout: TimeSpan.FromSeconds(60), maxAnswerChars: 4_000, history: 20))
    // A code agent administrators may make consultable about the types it works on, with a ceiling
    // within the application's.
    .ConfigureAgent(SampleProjectPlannerComposition.AgentId, agent => agent
        .ConsultableAsData(data => data.AllowRoutes(NhAssistantConsultationRoutes.AskerDecides))));
```

The names are a sketch; the shape is what AC2 to AC4 decide.

- **Application ceiling.** `UseConsultationsAsData` switches the feature on and names the routes data
  may use, whether data may generate request tools, and the highest limits. Agents created in the
  administration get this ceiling.
- **Code agent ceiling.** A code agent gets data consultations only with `ConsultableAsData`; its
  routes and limits intersect with the application's. B in it needs an agent that answers for itself
  with grants or acts with a technical account's rights; startup refuses it for an agent that answers
  to the subject's responsible person and for a ceiling outside the application's.
- **Not in any ceiling.** MCP exposure, the depth, the daily caps, grants and the consult turn's own
  rules stay code; data cannot name them.

### What administrators configure (AC14, AC15, AC13, AC16)

One data consultation per agent and subject type:

| Field | Data | Bound |
|---|---|---|
| Subject type | A registered type the agent works on, not `nh-system` for B | Unique per agent; not one the agent has in code |
| Description | Required, for the asking model | At most 1,000 characters |
| Instructions | Optional extra instructions of the consulted agent | At most 4,000 characters, stored with their hash |
| Who may ask | `MinimumRole` `reader`, `participant` or `approver`; an asker policy | The policy is one the application lists with `AddAgentPolicy`, else the agent's required policy, else the access policy |
| What it may do | Read-only, or proposals with A and one route at the subject | Routes within the ceiling; proposal role `participant` or `approver` |
| Proposal policy | An approval policy registered with `AddApprovalPolicy` | B refused with a policy whose approvers are only the initiator |
| Limits | Tool calls, timeout, answer length, history | The builder's bounds within the ceiling's maxima |
| Requests | The request tool on or off | Only when the ceiling allows requests |
| Classification | Of the answers in the asking conversation | At least the consulted agent's own classification |

A new data consultation starts read-only with the code defaults capped by the ceiling, the agent's
classification and no requests. At most 20 data consultations per agent.

### What stays code

| Stays code | Why |
|---|---|
| Whether data consultations exist at all, the routes and limits they may use (the ceilings) | Data combines what code made safe (D2) |
| Which code agents may get data consultations | The developer decides whether people reach an agent's memory and tools through other assistants |
| Consultations registered in code, beyond the CP21 switches | As code skills and triggers (D8) |
| MCP exposure | External clients run model turns; the attested MCP export stays static |
| Depth, daily caps, open B cap, consultation rules, grants | Platform guarantees, not per-agent settings |
| Approval policies, authorization policies, subject types and their resolvers | Building blocks data names |

### Storage and the snapshot (AC6, AC11)

- New table `AssistantAgentConsultationDefinition`, key `(AgentId, SubjectType)`: the fields above,
  `Version`, `CreatedAt`, `CreatedBy`, `UpdatedAt`, `UpdatedBy`, `Reason`, `ConcurrencyStamp`. Every
  save and removal changes the run configuration stamp in the same transaction.
- `AssistantConsultation` gains `ConsultationVersion` (nullable; `null` for a consultation in code),
  so each consultation record names the version it ran with.
- `NhAssistantStoredRunConfiguration` gains the rows; `Build` validates them after the run settings
  and skills and adds them with a `WithDataConsultations` copy of the run options, as `WithDataSkills`
  does. `NhAssistantConsultationOptions` gains `Source` (`code` or `data`) and `DataVersion`.
- Readers that use `registration.AgentRuns` then see data consultations without change: the consult
  service, routing, `OptionsOf`, the CP21 ceiling (`AllowedRoutes`), the known subject types of the
  agent list and the approval policy resolution.
- A host that only chats has no background refresh today. When data consultations are on, the chat
  turn start, the MCP tool listing and the admin tool list call `EnsureCurrentAsync` (one stamp read
  per second at most), and the background refresh runs on every host that serves the assistant (AC23).

### The tools of data consultations (AC7, AC8, AC12)

- A second catalog, `NhAssistantDataConsultationToolCatalog` (`assistant-consultations-data`), holds the
  tools of data consultations. The code catalog keeps its static model, now explicitly from
  `registration.CodeAgentRuns`, its startup attestation and its MCP export.
- The data catalog caches its model per snapshot instance and rebuilds it when the snapshot changes. A
  rebuild uses the same descriptor factory as the code catalog, runs `NhAiToolCatalogAttestation.Validate`
  on its own model and the descriptor shape checks that startup runs for other catalogs, and leaves out
  (with the problem `conflicts-with-tool`) a descriptor whose id another catalog already has.
- Exposure is always `Local | Agent`. The catalog never has an MCP descriptor, so the MCP validator and
  adapter skip it, and the count race between `Descriptors` and `CreateFunctions` cannot break the MCP
  export.
- Functions bind late: a data tool's function knows its agent, subject type and contract hash; the
  handler resolves the options from the snapshot in effect when it is called. When the consultation is
  gone or its contract hash differs (for example after a policy change), the call fails with
  `assistant-consult-agent-unavailable`, so a turn that started before a change never runs under a
  stale policy.
- Contract: the same input and output schemas, version 1, the same export name formula. The contract
  hash of a data tool changes only when its asker policies change. The data catalog's attestation hash
  changes with every data change; nothing pins it, and proposals never bind to the consult tool, which
  is read-only.
- Asking agents see the tool from their next turn when their selectors select it. A tool list fixed at
  a turn's start stays fixed for that turn.

### Who may consult whom (AC5)

The two keys stay. The data consultation is the consulted side's key; the asking side keeps selecting
the tool in its tool selectors: as data for an agent created in the administration, through an
override for an agent in code, or in code with an exact id or a prefix such as `assistant.consult.*`.
The consultation editor shows which agents select the tool now and links to their editors; the agent
editor's tool choice lists data consult tools, because `GET admin/tools` reads the catalogs live. The
asker's policy and live subject role are checked as today.

### Validation and problems (AC11)

On save the service refuses with field errors; on load the snapshot leaves data out or degrades it and
lists a problem of kind `consultation`:

| Problem | On load |
|---|---|
| `unknown-agent`, `invalid`, `not-worked-on`, `unknown-subject-type`, `unknown-agent-policy`, `conflicts-with-code`, `conflicts-with-tool`, `not-consultable` (the code ceiling was removed) | The consultation is out of effect; its tools disappear |
| `route-not-allowed` (the ceiling lost a route), `agent-answers-to-person`, `agent-has-no-rights`, `no-approver`, `unknown-policy` (the proposal policy is gone) | The consultation stays in effect without the routes that no longer fit, read-only when none is left |
| `limit-above-ceiling` | The consultation stays with its limits capped at the ceiling |

`agent-has-no-rights`: B on an agent that answers for itself without grants, such as every agent
created in the administration that answers for itself.

### Security: no escalation (AC1, AC4, AC13, AC14, AC15)

- **Reads stay the asker's.** The consult turn's scope (no grants, delegated from the asker, proposals
  only) is code; data cannot touch it (C2).
- **Who asks.** Both keys, the asker's policy (a listed policy, never none) and the live subject role,
  at least `Reader`; proposals need at least `Participant` in data, stricter than code.
- **Routes.** A and today's route use the asker's rights only. B uses the agent's own rights under the
  rules of CP10 to CP15, checked at every proposal: the asker's live rights at proposal time, hard
  four-eyes, approvers who pass the tool's approve policies, the open cap. For an agent created in the
  administration B means its technical account's rights, which an administrator already chose (D8 of
  the runtime overrides), and the proposal keeps the account it was recorded with (AC22); B is refused
  for one that answers for itself or to the subject's responsible person. For an agent in code B needs
  its own ceiling, because its trigger-only grants count for B (CP13) and were written for its runs.
- **Chains.** Depth, the chain rule, the self-consult rule and the daily caps are code. Data adds edges
  between agents, never depth; every hop uses the same asker's rights.
- **Policies.** Data names registered approval policies and listed authorization policies only. A
  tool-specific policy of the consulted agent keeps winning over the consultation's policy (CP5).
- **Classification.** A data consultation classifies its answers at least as the consulted agent's
  own data, so an answer is never stored or previewed more openly than the agent's own work.
- **Exposure.** Never MCP.
- **Shadow, off and stopped.** As today: an agent in shadow is never consulted.
- **The accountable path.** Closed first (AC1): the consult and request tools run only with the
  person's own request or a delegating initiator.
- **Administrators.** Every endpoint uses the admin policy; every change is audited. The texts are
  trusted application content, as an agent's own instructions in the administration are.

### Routes and the CP21 switches (AC10)

The routes of a data consultation are its setting. The agent-wide CP21 switches keep applying to every
consultation of the agent, code and data alike, through the same `NhAssistantConsultationRouting.Of`:
an operator switches a route off for the agent in one action. `SetConsultationRoutesAsync` already
reads the ceiling from `registration.AgentRuns`, so it covers data consultations; the Angular section
shows for every agent that has consultations.

### Changes while work is in flight (AC12)

- A running consultation finishes with the options it started with.
- Recorded proposals keep their policy snapshot, route, grant source and accountable owner, and can
  still be decided after the consultation changed or was removed. "Leave for approvers" is offered only
  while the consultation still allows a route at the subject (`OptionsOf`).
- A tool call after a removal or a contract change fails as described above.
- Removing an agent's run settings is refused while it has data consultations
  (`assistant-run-settings-in-use`, as for skills and triggers); a deleted administration agent leaves
  its rows as `unknown-agent` problems.

### Agents created in the administration (AC18, AC9)

- A data consultation needs the agent's run settings (`assistant-agent-not-configured` otherwise): they
  hold whom it answers to, its budget, run ceiling and approval policy, which a consultation uses.
- Defaults: the application ceiling; read-only; `MinimumRole` `reader`; the agent's required policy
  for askers; the agent's classification; code default limits capped by the ceiling; no requests.
- A consultation is in effect when it is saved. It does nothing on its own: it answers only when an
  asking agent selects its tool, so that selection is the moment it goes live.

### Administration API and contract (AC19)

| Surface | Change |
|---|---|
| `INhAssistantAgentConfiguration` | `SaveConsultationAsync(agentId, subjectType, NhAssistantDataConsultationRequest, actorId)` with `ExpectedVersion`, `RemoveConsultationAsync(agentId, subjectType, expectedVersion, actorId, reason)` |
| Endpoints | `PUT admin/agents/{id}/consultations/{subjectType}`, `DELETE admin/agents/{id}/consultations/{subjectType}` |
| `NhAssistantAgentConfigurationDto` | `CodeConsultations` (read-only: type, description, allowed routes, requests, exposure) and `DataConsultations` (`NhAssistantDataConsultationDto` with version, who and when, `InEffect`, `Problem`, the selecting agents), `ConsultationCeiling` for the agent or `null` |
| `NhAssistantRunCatalogDto` | `ConsultationCeiling` of the application or `null` |
| Problems | Kind `consultation` with the codes above |
| Error codes | `assistant-consultation-in-code` (409), `assistant-consultation-not-found` (404), `assistant-consultations-not-allowed` (409: the agent may not get data consultations), existing `assistant-consult-route-not-allowed` as field error `routes: not-allowed`, `assistant-configuration-limit`, `assistant-version-conflict` |
| Field errors | `subjectType`, `description`, `instructions`, `askerPolicy`, `minimumRole`, `proposalRole`, `routes`, `proposalPolicy`, `limits`, `dataClassification` |
| Public builder types | `UseConsultationsAsData`, `ConsultableAsData`, the ceiling builder and options; `NhAssistantConsultationOptions.Source` and `DataVersion` |
| Audit | `AdminAgentConsultationDefinitionChanged` (66): agent, subject type, version, instructions hash, `created`, `changed` or `removed`; `AgentConsulted` gains `ConsultationSource` and `ConsultationVersion` |
| npm | Models, `NhAssistantAdminApiService` methods, the mock API with consultations in `runCatalog` and the agent configuration |

All additions; the release notes list the migrations, the new catalog id, the late binding refusal
and, for applications with a discovery policy by exact ids, that data consult tools need the
`assistant.consult.` prefix allowed.

### Administration UI (AC20)

- **Consultations section** in the run configuration of every agent whose ceiling allows data
  consultations or that has code consultations. Each row: the subject type by name, source (code or
  data), description, a sentence on who may ask and who decides proposals, the agents that ask it,
  problems at the row. Code rows are read-only.
- **Form** (add and edit, the run configuration's form patterns, picker and field errors of U1): the
  subject type from the types the agent works on; description; instructions with a counter; *Who may
  ask?* (role and policy with descriptions); *What may it do?* (only answer, or answer and propose,
  with the routes as explained choices: "the person who asked decides in their chat", "the subject's
  approvers decide, with the asker's rights", "the subject's approvers decide, with this agent's
  technical account"; routes outside the ceiling shown disabled with the reason); the proposal role and
  approval policy with its summary and warnings (four-eyes keeps A from applying; initiator-only keeps
  B out); *Advanced*: limits, history, requests, classification.
- **Route switches** (CP21) move into the section as "switch a route off for all consultations of
  this agent", for every agent with consultations.
- **Flow view** (U6): the *when* step names "when another assistant consults it" with the count and an
  entry point; consultation problems map to that step.
- Loading, empty, error and disabled states, keyboard and focus, 375px without horizontal overflow,
  English and Dutch texts with aligned keys, as AGENTS.md requires.

### Sample case (AC21)

A new case, linked to SPM-278 (consultations) and SPM-296/298 (the project starter created at runtime):

- The sample calls `UseConsultationsAsData` with A and B and requests.
- The application, as an administrator would, saves a data consultation on the project starter (an
  agent created in the administration that answers to the operations account) about projects: A, and B
  with the operations account's rights, with a consultation policy. The project assistant selects
  `ConsultToolId(project-starter, project)` in code, which selects nothing until the data exists.
- A participant asks the project assistant whether a draft project can start; the starter answers with
  the participant's rights and proposes to start it; the participant decides it on the card (A) or,
  under four-eyes, another approver decides it and it executes with the operations account (B). A
  reader gets the answer without proposals. The principal factory that refuses people proves that no
  person's principal is rebuilt.
- Saving B on an agent that answers for itself is refused; a route outside the ceiling is refused with
  `routes: not-allowed`; after removing the consultation the next turn has no tool while the pending
  proposal can still be decided.
- The portal's assistant administration shows the section. Update the registry, the guidance rule
  `ai/nh-ai-agent-runs.md`, the consumer guide, the AI.Chat and AI.Chat.AspNet READMEs and
  `docs/sample-catalog.md`.

### Tests

- **Library (`NewHeap.Platform.AI.Chat.Tests`):** the accountable path refused (AC1); save
  validation per field and ceiling; the code and application ceilings and their intersection; startup
  refusals of a code ceiling; snapshot build with every problem, degraded routes and capped limits;
  the data catalog rebuild, attestation, conflicts and late binding with the contract check; MCP never
  lists data tools; CP21 switches over data consultations; B for an administration agent with a
  technical account and its refusals; the classification floor; in-flight proposals after change and
  removal; audit fields.
- **Relational (SQL Server and PostgreSQL, theories over `RelationalTestProvider`):** the store (save,
  version conflict, remove, stamp in the same transaction), `ConsultationVersion` on the record, the
  migrations.
- **Angular:** the section and form, routes within the ceiling, problems in the flow view, the admin
  API service, the mock.
- **Sample:** the new case on PostgreSQL and the portal check.
- Verification as AGENTS.md says: `npm run test:affected` while building, the full matrix once per
  train.

### Persistence and migrations

`AgentConsultationDefinitions` for SQL Server and PostgreSQL in `NewHeap.Platform.AI.Chat.SqlServer` and
`.PostgreSql`: the table `AssistantAgentConsultationDefinition` and the nullable column
`AssistantConsultation.ConsultationVersion`. Applications migrate; the release notes say so. No
migration of existing rows.

### Delivery order (AC24)

| Phase | Delivers |
|---|---|
| **P0** | The fix of the accountable path (AC1) with its test and release note, on its own. |
| **P1** | Ceilings, storage and migrations, snapshot and problems, the data catalog, the admin service and endpoints, audit, the sample case, guidance and release notes. |
| **P2** | The administration section, form, flow view and route switches, the mock API and the portal walk-through. |

## Risks

- **Both keys in administrators' hands.** For an agent created in the administration nobody in code
  consents to a consultation. The application ceiling is that consent; for code agents the per-agent
  opt-in keeps it.
- **Prefix selectors.** A code asker that selects `assistant.consult.*` picks up every data
  consultation; the guidance recommends exact ids or a reviewed prefix.
- **Cost and fan-out.** More consultable agents mean more model turns. Per-consultation limits, the
  consulted agent's budget, the asker's daily cap and the depth bound it.
- **Stale snapshots.** A change reaches other hosts within the stamp check; a turn keeps its tool list
  and a changed contract fails closed.
- **Description as prompt text.** An administrator's description enters other agents' tool lists; it is
  bounded and trusted like an agent's own instructions.
- **Memory with elevated reads.** As for code consultations: readers already see the memory the
  consultation reads; agents with elevated reads still need a review of what they keep.
- **Two places for routes.** A route can be off per consultation or per agent; the section shows both
  in one place with the reason.
- **Downstream migration** and the new catalog id for applications that list catalogs.

## Decision record

Each line: the question, the options, the recommendation and the status.

| # | Question | Options | Recommendation | Status |
|---|---|---|---|---|
| AC1 | Is the accountable path for consultation tools closed first? | (a) yes: the gate never allows `assistant.consult.*` and `assistant.request.*` through the accountable account's rights, and the handler refuses a call in an agent scope whose grant source is not `delegated`, as a separate fix with a test; (b) document it and leave it. | (a): the consultation plan promised that a turn without a delegating initiator never sees these tools, and agents created in the administration can already select them today. | Decided by Lars (2026-10-23): (a); done: the gap was real and is closed (fix/consult-accountable-path) |
| AC2 | Which agents can get consultations as data? | (a) only agents created in the administration; (b) every agent with a run configuration once the application opts in; (c) agents created in the administration, plus agents in code that opt in with `ConsultableAsData`. | (c): a developer decides whether people reach a code agent's memory and tools through other assistants, as `AllowConsultation` does today; administration agents are data anyway. | Decided by Lars (2026-10-23): (c) |
| AC3 | What is the ceiling in code? | (a) one application ceiling for everything; (b) an application ceiling (`UseConsultationsAsData`: routes, requests, maximum limits) for administration agents, and a per-agent ceiling for code agents that intersects with it; (c) no ceiling, only the platform's rules. | (b): the application decides what data may do at all; a code agent can narrow it. | Decided by Lars (2026-10-23): (b) |
| AC4 | Which routes may data use by default, and when B? | (a) the ceiling names its routes explicitly, read-only always allowed; B for administration agents only with a technical account, for code agents only through their own ceiling; (b) every route by default; (c) read-only only. | (a): today's route needs an application that rebuilds people's principals and B lends an agent's rights; both are the application's choice. | Decided by Lars (2026-10-23): (a) |
| AC5 | Who may consult whom? | (a) keep the two keys: the data consultation and the asking agent's tool selectors (data or override), with the editor showing who selects it; (b) the data consultation lists its askers, a third key; (c) saving a consultation adds it to askers' selectors. | (a): no new concept; the asking side is already data. | Decided by Lars (2026-10-23): (a) |
| AC6 | How are data consultations stored and versioned? | (a) one row per agent and subject type with `Version` and `ConcurrencyStamp` that changes the run configuration stamp, as data triggers; the version on each consultation record and in audit with the instructions hash; (b) a version history like skills; (c) columns on `AssistantAgent`. | (a): consultations have no candidates; record and audit trace which version answered. | Decided by Lars (2026-10-23): (a) |
| AC7 | How do the tools of data consultations appear? | (a) a second, snapshot-based catalog (`assistant-consultations-data`), agent and chat only, with late binding; the code catalog stays static and attested; (b) one dynamic catalog for code and data; (c) one generic consult tool with an agent argument. | (a): the attested MCP export and its startup checks stay untouched; (b) races between descriptors and functions in the MCP adapter; (c) changes every model's contract. | Decided by Lars (2026-10-23): (a), the data catalog may also be exported to MCP within the ceiling (AC8) |
| AC8 | May data consultations be offered to MCP clients? | (a) never; (b) when the ceiling allows, with attestation per snapshot and tool list change notifications. | (a): MCP clients cache tool lists and nothing notifies them; external model turns stay a code decision. | Decided by Lars (2026-10-23): (b): offered to MCP clients when the ceiling allows, with an attestation per snapshot and tool list change notifications |
| AC9 | Is a new data consultation in effect at once? | (a) yes; it is reached only once an asking agent selects its tool; (b) it starts off until an administrator switches it on; (c) shadow evaluation. | (a): a consultation does nothing on its own; the asking selector is the switch, and shadow agents are never consulted. | Decided by Lars (2026-10-23): (a) |
| AC10 | How do the CP21 switches relate to data consultations? | (a) the routes are set per data consultation, and the agent-wide switches keep switching routes off for every consultation of the agent, code and data, shown for every agent with consultations; (b) switches only for code consultations; (c) no routes per data consultation: data says read-only or proposals and the switches pick routes. | (a): one routing rule (`NhAssistantConsultationRouting.Of`) for both, and one action to switch a route off during an incident. | Decided by Lars (2026-10-23): (a) |
| AC11 | What happens to data that no longer fits? | (a) per part: a consultation whose agent, type, works-on or asker policy no longer fits is out of effect; a route that no longer fits is dropped (read-only when none is left) and limits are capped, each with a problem; (b) any problem takes the whole consultation out of effect; (c) startup fails. | (a): fails closed where it matters and keeps answers where only proposals changed, as CP21's ceiling already does. | Decided by Lars (2026-10-23): (a) |
| AC12 | What happens to work in flight after a change or removal? | (a) recorded proposals keep their snapshot and can be decided; running consultations finish; a later call of a changed or removed tool fails with `assistant-consult-agent-unavailable` (late binding with a contract check); (b) removal withdraws the consultation's pending proposals. | (a): a proposal is a fact with its own policy and guards, as run proposals of a changed skill; a stale contract never runs. | Decided by Lars (2026-10-23): (a) |
| AC13 | How are the answers of a data consultation classified? | (a) at least the consulted agent's own classification; (b) free, as in code; (c) always the agent's. | (a): an answer is the agent's data and must not be stored or previewed more openly. | Decided by Lars (2026-10-23): (b): free, as in code |
| AC14 | Which authorization policies may askers need? | (a) one the application lists with `AddAgentPolicy`, else the agent's required policy, else the access policy; (b) any registered policy; (c) always the agent's required policy. | (a): data names what the application listed for administrators, as run and view policies do. | Decided by Lars (2026-10-23): (a) |
| AC15 | Which limits and roles may data set? | (a) the builder's bounds within the ceiling's maxima; `MinimumRole` at least `reader`; proposal role at least `participant`; at most 20 data consultations per agent; description required; (b) exactly the builder's bounds. | (a): data is never less safe than code and slightly stricter where a reader could otherwise cause proposals. | Decided by Lars (2026-10-23): (b): exactly the builder bounds |
| AC16 | May data consultations generate request tools? | (a) when the ceiling allows requests, off by default; (b) never; (c) always. | (a): a request starts a run as `POST threads/start` does for a participant; the application decides. | Decided by Lars (2026-10-23): (a) |
| AC17 | May administrators change consultations registered in code? | (a) no, only the CP21 switches; data adds consultations for other subject types; (b) they may override description and instructions. | (a): as code skills and triggers, which data cannot change. | Decided by Lars (2026-10-23): (a) |
| AC18 | Does a data consultation of an administration agent need run settings? | (a) yes, `assistant-agent-not-configured` otherwise, and run settings cannot be removed while it exists; (b) saving creates default run settings; (c) platform defaults without run settings. | (a): run settings hold the accountability, budget, ceiling and approval policy a consultation uses, as for skills and triggers. | Decided by Lars (2026-10-23): (a) |
| AC19 | How is a data consultation audited? | (a) a new `AdminAgentConsultationDefinitionChanged` with version, subject type and instructions hash, and `AgentConsulted` with source and version; (b) reuse `AdminAgentRunSettingsChanged`. | (a): one event kind per kind of data, as for skills and triggers. | Decided by Lars (2026-10-23): (a) |
| AC20 | Where do administrators configure consultations? | (a) a Consultations section in the run configuration, code rows read-only, the CP21 switches in it, and the flow view's *when* step; (b) a page of its own. | (a): one place per agent, as the flow view promised. | Decided by Lars (2026-10-23): (a) |
| AC21 | Which sample evidence? | (a) a new case (the next free id, SPM-311 when this was written) linked to SPM-278 and SPM-296/298, with the project starter consulted with A and B; (b) extend SPM-278; (c) extend SPM-298. | (a): the evidence is the run configuration as data (ceiling, store, problems, dynamic catalog), as SPM-295 to SPM-298 and SPM-308 were new cases; SPM-278 already holds three flows. | Decided by Lars (2026-10-23): (a) |
| AC22 | What does an administration agent consulted with B answer to? | (a) its technical account at proposal time, kept on the proposal (as CP30), with the runtime override included; (b) the account at execution. | (a): the evidence binds the accountable owner, as for run proposals. | Decided by Lars (2026-10-23): (a) |
| AC23 | How fresh is the snapshot on hosts that only chat? | (a) chat turns, MCP listing and the admin tool list check the stamp at most once per second when data consultations are on, and the background refresh runs on every assistant host; (b) the 30-second background refresh only, on hosts that run agents. | (a): a chat-only host otherwise never sees a new or removed consultation. | Decided by Lars (2026-10-23): (a) |
| AC24 | In which order is it delivered? | (a) P0 the fix of AC1 on its own, P1 library, sample and guidance, P2 administration UI and mock; (b) everything at once; (c) library and UI together after the fix. | (a): the fix matters today; P1 keeps the library and its sample evidence together as AGENTS.md requires. | Decided by Lars (2026-10-23): (a) |
| AC25 | How is AC15 (b), "exactly the builder's bounds", read next to the ceiling's maximum limits of AC3 and AC11? | (a) roles and limits exactly as `AllowConsultation` allows them (`MinimumRole` and the proposal role reader, participant or approver; 0 to 32 calls, 1 second to 10 minutes, 200 to 32,000 characters, 0 to 200 messages); `MaxLimits` of a ceiling stays optional and only lowers them (default: the builder's bounds), with `limits: not-allowed` on save and `limit-above-ceiling` on load; the description is required and an agent has at most 20 consultations as storage limits of the administration; (b) no ceiling maxima at all; (c) also an optional description and no count limit. | (a): AC3 (b) and AC11 (a) name the ceiling's maxima, so they remain for applications that want them; without them data has exactly the builder's bounds. A description is the only text the asking model reads about when to consult an agent nobody described in code, and 20 matches the triggers. | Decided by Lars (2026-10-23): (a) |
| AC26 | How does a data consultation reach MCP clients under AC8 (b)? | (a) per consultation: `exposeToMcp`, off by default, allowed only when the ceiling (both ceilings for an agent in code) calls `AllowMcp()`; the catalog is a generic `INhAiChangingToolCatalog` whose every view is attested before it enters the export, and the MCP SDK's tool collection signals `notifications/tools/list_changed`, which 2026-07-28 clients receive on a `subscriptions/listen` stream and stateless transports cannot receive; (b) every data consultation is exported once the ceiling allows MCP; (c) a catalog type of the assistant only. | (a): as `Exposure(Mcp)` in code, MCP stays a choice per consultation; a generic interface keeps the MCP export and the agent adapter free of assistant types and lets other runtime catalogs reuse it. | Decided by Lars (2026-10-23): (a), plus an integration test that shows `notifications/tools/list_changed` over Streamable HTTP (`subscriptions/listen`) and the safe failure of a stale tool list; the test is to build |
| AC27 | Which problems does a consultation get when the ceiling no longer allows requests or MCP? | (a) `requests-not-allowed` and `mcp-not-allowed`: it stays in effect without the request tool or the MCP exposure; (b) `route-not-allowed` for both; (c) out of effect. | (a): AC11 (a) degrades per part; the administration can name the part. | Decided by Lars (2026-10-23): (a) |
| AC28 | Which field errors does saving return? | (a) `routes: not-allowed` outside the ceiling and `routes: invalid` for the route with the agent's own rights on an agent without rights of its own; `proposalPolicy: not-found` or `invalid` (approvers only the initiator); `limits: invalid` outside the builder's bounds and `not-allowed` above the ceiling; `allowRequests` and `exposeToMcp: not-allowed`; `askerPolicy: not-found`; `subjectType: not-found` or `invalid` (not worked on, or a tool name taken); a new field error value `not-allowed`; (b) one generic `invalid`. | (a): the administration explains each refusal at its field (U1). | Decided by Lars (2026-10-23): (a) |
| AC29 | Where is "else the agent's required policy" of AC14 resolved? | (a) in the snapshot: without an asker policy the consultation's policies are the agent's required policy at load, so a changed required policy changes the tool's contract and a turn built before fails closed; (b) at call time. | (a): the descriptor carries the policies the gate checks, and AC12 already fails a changed contract closed. | Decided by Lars (2026-10-23): (a) |
| AC30 | Which classification does a consultation without one get? | (a) the agent's classification at every load (stored as none), as data skills do; (b) the agent's classification at save, stored. | (a): AC13 (b) frees the value; without one it follows the agent. | Decided by Lars (2026-10-23): (a) |
| AC31 | When is the catalog `assistant-consultations-data` registered? | (a) only with `UseConsultationsAsData`; (b) always, empty. | (a): applications without consultations as data see no new catalog. | Decided by Lars (2026-10-23): (a) |
| AC32 | What does `conflicts-with-tool` compare? | (a) the tool ids and export names of every catalog registered at startup, read once, on save and on load; (b) ids only. | (a): the agent adapter and the MCP export refuse duplicate export names. | Decided by Lars (2026-10-23): (a) |
| AC33 | What does `ConsultableAsData()` without a configuration allow? | (a) answers only: no routes, requests or MCP, the builder's limits within the application's; (b) the application's ceiling. | (a): a code agent opts in to each route explicitly, as AC4 (a) says. | Decided by Lars (2026-10-23): (a) |
| AC34 | What does the sample's project starter need to propose in a consultation? | (a) `Execute` autonomy, so its model is offered the status tool; every change still waits for a decision; (b) keep `Propose` and let consultations offer mutating tools to `Propose` agents. | (a): the library offers mutating tools only to agents with `Execute`, in runs and consultations alike; changing that is outside P1. | Decided by Lars (2026-10-23): (b): consultations offer mutating tools as proposals also to agents with `Propose` or `Simulate` autonomy, runs stay as they are, a consultation without proposal routes never gets them, and the sample's project starter goes back to `Propose`; to build |
| AC35 | When does the run configuration show the Consultations section, and what does it say when an agent cannot get consultations as data? | (a) for an agent with consultations in code or as data and, once the run catalog reports an application ceiling, for every agent, with the reason it cannot get one: an agent in code that is not consultable as data, an agent created here without run settings, the ceiling's maximum, or every kind of object it works on already has one; no section for an agent without consultations in an application without consultations as data; (b) only for agents whose ceiling allows data consultations; (c) always. | (a): the section explains a closed door where the application offers consultations, and adds nothing to applications that never do. | Decided by Lars (2026-10-23): (a) |
| AC36 | Where do the CP21 route switches go? | (a) inside the Consultations section, below the list, for every agent that has consultations in effect, reading the routes again after each change of the run configuration, with texts that speak of the agent's consultations instead of its code; (b) their own section after it, for agents in code only. | (a): AC10 and AC20 make one switch apply to code and data alike, in one place. | Decided by Lars (2026-10-23): (a) |
| AC37 | How does the form offer the routes? | (a) one check box per route with its explanation, at most one of the two routes at the subject (choosing one drops the other); choosing to propose starts with the first route the agent may use; a route the ceiling does not name, the route with the agent's account for the whole application, for an agent that answers to the subject's responsible person or for an agent created here that answers for itself, is disabled with the reason; a stored route that no longer fits stays checked and enabled, marked invalid, so it can be cleared; an agent in code that answers for itself is not blocked, because only the server knows its grants; (b) one radio group of route combinations. | (a): every route reads as a choice of its own, as the plan's form describes, and the server stays the judge of what the client cannot see. | Decided by Lars (2026-10-23): (a) |
| AC38 | Which approval policy warnings does the form give? | (a) four eyes with the asker deciding, and a policy of the initiator only with the route with the agent's account, both for the consultation's policy or else the agent's; warnings, while the server refuses the second with `proposalPolicy: invalid`; (b) none. | (a): the plan asks for both, and they explain a refusal before it happens. | Decided by Lars (2026-10-23): (a) |
| AC39 | How does the form treat limits, requests and MCP? | (a) under Advanced, which opens itself for an error: an empty limit starts at the code's default within the ceiling (shown as the placeholder), each checked from the builder's minimum to the lower of the builder's and the ceiling's maximum; requests and MCP as check boxes only when the ceiling allows them, otherwise a sentence why, and a stored choice the ceiling no longer allows is saved as off; (b) every field always visible. | (a): the rarely changed options stay out of the way, as in the run settings form. | Decided by Lars (2026-10-23): (a) |
| AC40 | How does the editor of an asking agent open from a consultation? | (a) `openAgent` of the run configuration, which `nh-assistant-admin-agents` handles by opening that agent's editor and returning to the consulted agent's run configuration when it closes; (b) no link, names only. | (a): AC5 says the editor links to the askers, the second key. | Decided by Lars (2026-10-23): (a) |
| AC41 | What can an administrator do with a consultation of an agent that may no longer get any (`not-consultable`)? | (a) remove it only: no edit in the section and no fix in the flow; (b) edit and see the refusal. | (a): any save is refused without a ceiling. | Decided by Lars (2026-10-23): (a) |
| AC42 | What does the flow's when step show of consultations? | (a) each consultation with whether it only answers or also proposes and how many agents ask it, an edit entry for those defined as data, an entry to add one when that is possible, and problems of kind `consultation` with the subject type's name; (b) a count with one entry point. | (a): the step reads like the triggers above it and every problem has its fix. | Decided by Lars (2026-10-23): (a) |
| AC43 | How does the mock API play consultations as data? | (a) `consultationsAsData` on the admin scenario, `consultableAsData`, `consultationDescriptions` and `dataConsultations` per agent; consultations in code from the route map and the descriptions (no requests, local and agent exposure); the same checks and codes as the server on save and on every read, with the known subject types of the scenario and `nh-system`, and an agent in code that answers for itself assumed to have grants; data tools in `GET admin/tools`; (b) a mock without the checks. | (a): hosts and specs see the refusals and problems they will see on the server. | Decided by Lars (2026-10-23): (a) |
| AC44 | Which sample evidence does P2 add? | (a) SPM-311 extended: the management playground's scenario has the project starter with its consultation, as the API's seeder, and the registry, catalog and guidance name the administration; (b) a new case. | (a): it is the same case seen in the administration, as AC21 planned. | Decided by Lars (2026-10-23): (a) |
| AC45 | Which existing texts change? | (a) the route switches (`code`, `not-allowed`, `not-allowed-hint`, `action.code`, `explain-change.code-on`, `code-off`), `assistant-consult-route-not-allowed`, `assistant-configuration-limit` and `assistant-run-settings-in-use` name consultations as data too, in English and Dutch, listed in the release notes for applications that override them; (b) keep them. | (a): they were true for code only and would mislead next to data consultations. | Decided by Lars (2026-10-23): (a), and the release notes' breaking change also lists the changed texts of `assistant-consult-route-not-allowed`, `assistant-configuration-limit` and `assistant-run-settings-in-use`; to build |
