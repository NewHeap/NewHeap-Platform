# Trigger Event Filters Plan

This document records the decisions behind named filters on trigger events that a data trigger
selects, so an agent is not started again by a change it made itself. It builds on the
[run configuration as data](ai-agent-run-configuration-plan.md) and the
[agent off state](ai-agent-off-state-plan.md). It was checked against `main` at `55a328a3` on
2026-10-22.

## Status

Implemented on 2026-10-22 on the branch `feature/trigger-event-filters`. Lars chose the design
(option C: named filters on the trigger event, selectable per data trigger in the administration)
and added the subject to the filter context during the implementation. Sample case SPM-308 shows it.
The decisions marked "For Lars to review" were taken during the implementation and wait for his
review.

## Problem

A code trigger can filter per trigger (`NhAssistantTriggerBuilder.When`). A data trigger has no
filter of its own: only the event's `When` exists, which applies to every data trigger of every
agent on the event and does not know which agent it decides for. An agent whose tool changes data
that an event reports therefore starts itself again on its own change.

## Design

- Code registers a filter on the event: `Filter(name, description, (evt, ctx) => bool)` on
  `NhAssistantTriggerEventBuilder<TEvent>`. The name is lowercase dash-case, at most 64 characters
  and unique per event; the description is required and at most 300 characters.
- `NhAssistantTriggerFilterContext` names `AgentId`, `SkillId`, `TriggerId`, `AgentActorId` and
  `Subject`. `AgentActorId` is the actor the agent acts as in its runs: the
  `NhAiInvocationContext.ActorId` of its tool calls, `NhAiAgentInvocationScopes.Current.AgentActorId`
  there and `NhAssistantAgentDefinition.ActorId`. A library test asserts both inside a tool call of
  the agent's run.
- A data trigger stores the names it applies, ordered and distinct, as a JSON array in the new
  nullable column `FiltersJson` (migration `TriggerEventFilters`, SQL Server and PostgreSQL).
- Order at publish: agent and skill state (off ignores, stopped rejects), the event's `When`, the
  event's `Subject` function, the selected filters (all must accept), then message, initiator,
  dedupe and payload. A refusal is `ignored` with `filtered`, not audited.
- Saving a filter the event does not register fails with `assistant-trigger-filter-not-found`
  (`400`) and the field error `filters: not-found`. A stored filter the code no longer registers
  fails closed with `filter-missing` and the problem `unknown-filter`.
- The run catalog lists each event's filters; the data trigger request and response carry
  `filters`; the Angular trigger form offers the chosen event's filters as check boxes.

## Decision record

One line per decision: the decision, the reason, and its review status.

| # | Decision | Reason | Review |
|---|---|---|---|
| TF1 | The filters decide once per data trigger with the context of the trigger's own agent; its shadow variants (`ShadowOf`) receive what that agent receives, as they do for `When`. | A variant is evaluated against the live agent on the same events; a per-variant decision would make the comparison unequal. A variant's tools only propose in its shadow thread, so it does not cause the loop the filter prevents. | For Lars to review |
| TF2 | A trigger with a missing filter stays registered in the snapshot (so publishing gives a receipt `ignored`/`filter-missing` and logs a warning per event) and is also listed as the problem `unknown-filter`, so the administration shows it out of effect. It is the only problem that keeps its trigger registered. | Lars asked for a distinct receipt reason; leaving the trigger out like `unknown-event` would give no receipt at all, and without the problem the administration would show it as in effect. | For Lars to review |
| TF3 | The missing-filter check runs after the event's `When`: an event `When` refuses is `filtered` and logs nothing. | The event concerns no trigger then; logging a warning for it would only add noise. | For Lars to review |
| TF4 | Unknown filters on save return the dedicated code `assistant-trigger-filter-not-found` (HTTP `400`) with the field error `filters: not-found`, instead of the general `assistant-validation`. Other invalid filter input (bad name, more than 10, filters on a schedule) is `assistant-validation` with `filters: invalid`. | An import can tell an unknown filter apart from malformed input, and the form still marks the field. `400` because the request names something the event lacks, like `event: not-found`. | For Lars to review |
| TF5 | At most 10 filters per data trigger (`MaxTriggerFilters`); no limit per event. The column holds at most 1,000 characters. | Ten filters with the longest names fit with room to spare; an application decides how many filters its events offer. | For Lars to review |
| TF6 | A name given twice in the request counts once; the order of the request is kept. The Angular form sends the selected filters in the order the event registered them, those it no longer offers last. | Order is stored as chosen but does not change the outcome (AND); the event's order keeps forms and stored values stable. | For Lars to review |
| TF7 | There is no separate run-configuration export or import in the library; an application imports through `INhAssistantAgentConfiguration.SaveTriggerAsync`, which carries `Filters` and refuses an unknown filter (the "conflict on import"). Data stored by another host or an earlier deploy is reported through `NhAssistantRunConfigurationProblems.UnknownFilter`. `NhAssistantTriggerConflictNames` (what happens while a run is busy) is unrelated and unchanged. | The sample's package import uses the same call; a second import path would duplicate the checks. | For Lars to review |
| TF8 | The catalog reuses `NhAssistantRunBlockDto` (name and description) for a filter: `NhAssistantRunBlockDto.Filters` is a list for a trigger event and `null` for the other building blocks. | No new contract type for two fields; `null` keeps the other blocks' JSON honest. | For Lars to review |
| TF9 | `NhAssistantDataTriggerDto.Filters` is an init property and `NhAssistantDataTriggerRequest.Filters` an optional last positional parameter, as earlier additions to these records were made. | Source compatible for callers that name their arguments; the binary change is in the release notes. | For Lars to review |
| TF10 | The subject function runs before the filters only for a data trigger with filters, and its result is reused by the fire, so it still runs once per trigger. Code triggers are unchanged. | Lars's addition: a filter needs the subject; computing it once keeps the cost and the side-effect profile as before. | For Lars to review |
| TF11 | The library does not stamp anything on application events. The sample shows the pattern: its status tool stamps `NhAiInvocationContext.ActorId` and `NhAiAgentInvocationScopes.Current.Subject.Subject` on the status event (`ProjectAgentCause`), and the filter skips only when both match the context. | Events belong to the application; the ambient scope is already public and verified in a library test. Inside a proposal placed at another object, the ambient subject is that object; the sample documents the run's subject as "the subject the agent works in". | For Lars to review |
| TF12 | The Angular form blocks saving while a selected filter is missing from the catalog (client-side `filters: not-found`) and shows such a filter as a checked, invalid check box until it is cleared. | The server would refuse the save anyway; the form says why before it sends. | For Lars to review |
| TF13 | The sample's status follow-up trigger names the planner's rule-based `task-follow-up` skill rather than a model skill. | The sample test must not depend on the shared host's scripted model; the filter, not the skill, is what the case shows. | For Lars to review |

## Verification

- Library: `AssistantTriggerEventFilterTests` (both providers): the context incl. `AgentActorId` and
  `Subject` and their equality with the tool call's invocation context and ambient scope, the own
  change on the same subject ignored and on another subject started, AND of filters after `When`,
  the order subject, filter, message, dedupe (also for a subject known only when the event fires),
  the stored column, version bump and audit, refusal of unknown and invalid filters, fail-closed
  `filter-missing` with `unknown-filter`, and the admin JSON contract.
- Angular: `nh-assistant-trigger-filters.spec.ts` and the admin API service spec.
- Sample: `The_planner_is_not_started_again_by_its_own_status_change_of_a_project_but_by_every_other_change`.
