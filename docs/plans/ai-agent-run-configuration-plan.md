# Agent Run Configuration Plan

This document is the architecture and implementation plan for agent run configuration as data: skills and the
triggers that start them, defined at runtime on agents registered in code and on agents created in the
administration, with versions that an administrator puts live directly or evaluates in shadow first. It follows the
[agent skills plan](ai-agent-skills-plan.md) and the runtime overrides of the
[agent runtime overrides plan](ai-agent-runtime-overrides-plan.md). It was checked against `main` at `0490e95e` on
2026-10-19.

## Status

Accepted on 2026-10-19; in progress. Lars decided the [questions](#decision-record). Phases A1 to A4 follow the
[delivery order](#delivery-order).

| Phase | Status |
| --- | --- |
| A1 | Implemented: building blocks, data skills and data triggers on agents in code, the snapshot with its stamp, the store, the `AgentRunConfiguration` migrations, `INhAssistantAgentConfiguration` with the admin endpoints, data schedules, and SPM-295. |
| A2 | Implemented: run settings as data for agents created in the administration, the `Accountable` grant source with `ActWithAccountableRights()`, the `AgentRunSettings` migrations and SPM-296. |
| A3 | Open: a change of a live data skill takes effect at once in A1. |
| A4 | Open: the administration screens and the mock API. |

## Problem

Agent runs are configured in code: `ConfigureAgent` declares an agent's skills and `AddTrigger`, `AddScheduledTrigger`
and `AddWebhookTrigger` declare what starts them. Changing or adding a skill needs a developer and a deploy. An
application whose agents and skills are data, created in screens, shipped in packages and linked between
installations, cannot give those agents runs. Agents created in the administration chat only.

## Decision record

| # | Decision | Reason |
| --- | --- | --- |
| D1 | **Skills and triggers become data** on an agent: for agents registered in code and for agents created in the administration. | Applications that manage agents as data get runs without a deploy. |
| D2 | **Building blocks stay code and data names them:** rule handlers (`AddRuleHandler<T>(name)`), trigger events (`AddTriggerEvent<TEvent>(name, ...)`), subject sets for schedules (`AddSubjectSet(name, ...)`), approval policies, model profiles, subject types, briefings and webhooks. | Data combines what code made safe; it never introduces a new kind of access, action or event. |
| D3 | **Triggers as data link a named event or a schedule to a skill of an agent**, with the conflict behavior and debounce. The event's subject, message, initiator, dedupe key and payload come from its code definition. Webhooks stay code. | A trigger in code is C# functions; their result is what data cannot hold. |
| D4 | **An agent created in the administration has its run settings as data:** accountability (itself, the subject's responsible person or a technical account), approval policy, budgets, run ceiling, retries, gateway reads, triage profile and rule handler by name. **Grants never become data.** | The application decides per agent without a deploy; extra rights stay reviewed code. |
| D5 | **Such an agent acts with the rights of the account it answers to,** bounded by its tool selectors: a new grant source `Accountable` next to `Delegated`, `DelegatedRead` and `Agent`. An agent in code opts in with `ActWithAccountableRights()`. | An agent's rights are then managed as a person's are; an agent that answers to itself has none. |
| D6 | **Saving a change to a live data skill offers a choice:** the new version goes live at once, or it runs in shadow next to the live version on the same triggers until an administrator promotes or discards it. A new skill and a new trigger always start in shadow. | Small corrections need no evaluation; real changes get one. |
| D7 | **Instructions are stored with the skill's version** in NewHeap, with their hash; the application fills them, for example from a skill package. | Every run is traceable to the exact version it used. |
| D8 | **The same limits as code:** saving validates what startup validates (tool selectors within the agent's, known profiles, policies, rule handlers, events and subject sets), every change is a version with an audit event, and code skills and triggers cannot be changed as data. | Data must never be less safe than code. |

## Design

### Building blocks in code

```csharp
assistant
    .AddTriggerEvent<ProjectTaskCompleted>("project.task-completed", e => e
        .Description("A task of a project was completed.")
        .Subject(evt => new NhAiSubjectRef("project", evt.ProjectId.ToString("D")))
        .Message(evt => $"Task {evt.Title} was completed.")
        .Dedupe(evt => evt.TaskId.ToString("N"))
        .Payload(evt => new { evt.TaskId, evt.CompletedBy }))
    .AddSubjectSet("active-projects", "Active projects", (services, ct) => ...)
    .AddRuleHandler<ProjectTaskFollowUpRules>("project-task-follow-up");
```

Code triggers keep working; a trigger event is a code trigger without a target.

### Data

| Table | Holds |
| --- | --- |
| `AssistantAgentRunConfiguration` | The run settings of an agent created in the administration (D4); one row per agent, versioned. |
| `AssistantAgentSkillDefinition` | One row per version of a data skill: description, instructions and hash, tool selectors, profile, rule handler name, ceiling, budget, approval policy, status `live`, `candidate` or `superseded`, who and when. |
| `AssistantAgentTriggerDefinition` | One row per data trigger: id, agent, skill, an event name or a schedule (cron, time zone, subject set, message), conflict, debounce, shadow, version, who and when. |
| `AssistantRunConfigurationStamp` | One row whose value changes with every change, so every host reloads. |

### Effective configuration

The registration keeps the code configuration. `NhAssistantRunConfigurations` builds the effective configuration from
code and data into an immutable snapshot that `AgentRuns` and `Triggers` return, so every reader keeps its synchronous
access. It reloads at startup, after every change in the process, and whenever the stamp changed: checked before every
fire and every run start, at most once per second, and by a background refresh. Data that no longer validates (a
removed profile, policy or event) is left out of the snapshot and reported in the administration instead of failing
startup.

### Shadow and versions

- A new data skill starts in shadow (its stored shadow mode is `true`), and so does a new data trigger: its runs go to
  the shadow thread until an administrator puts it live.
- A live skill's change is saved with `apply: live` (a new version, at once) or `apply: shadow` (a candidate). A
  candidate runs as a second, shadow run on the triggers that name its skill; runs record the skill version. Promoting
  makes the candidate the live version; discarding removes it. Routing by rules or triage reaches the live version.

### Rights of the accountable account

The invocation gate checks, after gateway reads and the initiator's delegation and before code grants, whether the scope
allows the accountable account's rights: the account's principal from `INhAiPrincipalFactory` must pass the tool's
policies against the subject. The grant source `Accountable` is recorded on the call, the proposal and the audit; reads
are shielded from thread readers without the tool's policies, as for agent grants.

### Administration and API

`INhAssistantAgentConfiguration` serves applications (for example a package import) and the administration endpoints
under `admin/agents/{id}`: the catalog of building blocks, skills with versions, candidates, promotion and discarding,
triggers, and the run settings of an administration agent. The agents page of `@newheap/platform-ai-chat/admin` edits
them.

## Delivery order

| Phase | Delivers | Sample |
| --- | --- | --- |
| **A1** | Building blocks in code, data skills and data triggers on code agents, the effective snapshot with reload, storage and migrations, the configuration service and admin API, starting in shadow | An administrator adds a skill and its trigger to the project planner without a deploy, evaluates it in shadow and puts it live |
| **A2** | Run settings as data for administration agents, the `Accountable` grant source and `ActWithAccountableRights()` | An administrator creates an agent that follows up new projects with its technical account's rights |
| **A3** | Versions with the choice at save, candidates in shadow next to the live version, promotion, discarding and the comparison | A changed skill is evaluated in shadow and promoted |
| **A4** | The administration screens, the mock API, texts and documentation | The sample's administration edits skills, triggers and run settings |

## Out of scope

Webhook triggers as data, hand-offs and consultations of administration agents, shadow variants (`ShadowOf`) as data,
filters on event payloads as data, and changing code skills or code triggers as data.
