# Agent Off State Plan

This document records the decisions behind three agent features: an **off** state for agents and
skills with a starting state in code (`StartsOff()`), a data classification per agent and per skill
for the model calls of their runs, and a model profile for agents created in the administration. It
builds on [agent control](ai-agent-control-plan.md), [agent skills](ai-agent-skills-plan.md),
[runtime overrides](ai-agent-runtime-overrides-plan.md) and the
[run configuration as data](ai-agent-run-configuration-plan.md). It was checked against `main` at
`6c152e0b` on 2026-10-22.

## Status

Implemented on 2026-10-22. Sample case SPM-284 shows the off state and `StartsOff()`. The consumer
guide `ai-agent-runs` and the sample are authoritative from now on. Every decision below was taken
during the implementation where the request left a choice open and is for Lars to review.

## Problem

Pausing queues events per thread and resuming runs them all, which after weeks is a wave of stale
work. Stopping cancels running work, withdraws proposals and refuses chat. `Enabled(false)` in code
reads `configured-off` and cannot be switched on at runtime. None of them fits an agent or skill that
should simply not work on its own for a while, or that a deploy should bring in switched off until an
administrator switches it on.

## Decision record

One line per decision: the decision, the reason, and its review status.

| # | Decision | Reason | Review |
|---|---|---|---|
| O1 | The state `off` is stored in the existing `RunState` columns of the agent and the skill state; no migration. | The columns hold 16 characters and the state is one more value; a migration would add nothing. | For Lars to review |
| O2 | Triggers ignored while off are reported as `ignored` with `agent-off` or `skill-off` (`NhAssistantTriggerReasons`), not audited, and not logged per event; a schedule reports one ignored receipt per tick without reading its subjects. | The request asks for the same treatment as `filtered`; one receipt per tick keeps the subjects query unevaluated. | For Lars to review |
| O3 | What an agent takes on by itself is ignored while off: events, schedules, webhooks, follow-ups, hand-off answers and replays. Work a person asks for still starts: manual starts, messages typed in a thread and requests through another assistant. Continuations of accepted work (a resume, a move to the shadow thread) are not triggers. | Chat and `POST threads/start` must stay possible; a request is a person's start through another assistant. | For Lars to review |
| O4 | A hand-off to an agent that is off is refused with `assistant-agent-off` (audited like other hand-off refusals) instead of being ignored. | The source's model and rules need to know that nobody took the work; a hand-off receipt has no ignored outcome. | For Lars to review |
| O5 | A replay into a shadow variant that is off stops with `assistant-agent-off`, as it stops for a paused variant. | A replay is automatic evaluation work; an off variant should cost nothing. | For Lars to review |
| O6 | Switching off cancels nothing: running runs and runs that were already queued finish with their inputs. | The request says running runs finish; a queued run was accepted work and pausing is what holds queued runs back. | For Lars to review |
| O7 | Switching off ends the inputs that waited from an earlier pause, except those a queued or running run of the thread will claim; switching on from off never calls the resume of waiting inputs. | "Switching on replays nothing": events that waited during a pause would otherwise run weeks later. | For Lars to review |
| O8 | A run whose rules route to a skill that is off ends `cancelled` with `assistant-skill-off` and nothing waits for the skill, as for a stopped skill; triage and `use_skill` already offer active skills only. | Rules must not route to an off skill, and an off skill keeps no backlog. | For Lars to review |
| O9 | Switching on is `ResumeAsync` / `ResumeSkillAsync` (state `active`); only `SwitchOffAsync` and `SwitchOffSkillAsync` are new members. The administration labels the action "Switch on" for an agent or skill that is off. | `active` already means "works"; a second name for the same transition would make the state endpoint ambiguous. | For Lars to review |
| O10 | The kill switch keeps mapping to stop and resume; disengaging it also switches an agent that is off on. | The kill switch keeps its meaning; it never reads the off state. | For Lars to review |
| O11 | Switching off is audited as the new kind `AdminAgentSwitchedOff` (62) with result code `off`; switching on as `AdminAgentRunsResumed` (`resumed-0`). There is no live channel for agent state; the administration updates from each action's response, as for the other states. | Same audit shape as the other state changes; the existing state changes have no live updates either. | For Lars to review |
| O12 | `StartsOff()` applies when the agent row is inserted at startup. For skills, every skill in code gets a state row at its first start (active, or off with `StartsOff()`), so the row marks the skill as seen. A skill deployed before this version that gets `StartsOff()` in the upgrade deploy therefore starts off once. | A skill without a row was active, so "seen before" is unknowable for older skills; recording rows from now on makes later deploys exact. | For Lars to review |
| O13 | Agents and data skills created in the administration start off only when asked to (`startsOff` on create, default `false`); the create forms offer "Switch on now", ticked by default. `startsOff` on a change of a data skill is a field error. | Consistent opt-in with `StartsOff()` in code and no change for existing callers; data skills already start in shadow. | For Lars to review |
| O14 | An agent created off in the administration records its creator and time as the state change; a code agent that starts off has no change recorded, and the administration says it started off in code. | Shows who decided the state without inventing an actor for code. | For Lars to review |
| O15 | `NhAssistantCodeRunSettingsDto.StartsOff` and `NhAssistantCodeSkillDto.StartsOff` show the starting state in code in the run configuration; the current state stays in the agent's state. | The administration can explain why an agent is off without mixing code and runtime state. | For Lars to review |
| O16 | The sample deploys the project planner's evaluation variant off and extends SPM-284 instead of adding a case; its evaluation test switches the variant on and off again. | Evaluation variants are the natural "deploy now, switch on later" agents, and one control case keeps the registry free of duplicates. | For Lars to review |
| C1 | The classification of a model call is the skill's, then the agent's, then `Internal`. It covers the agent's runs, people's turns in its threads and its consultations; triage uses the agent's own. Plain chat conversations with the agent stay `Internal`. | "Most specific wins"; triage runs before any skill; a plain chat is no work of the agent's runs. | For Lars to review |
| C2 | The enforcement is the existing profile resolution: a profile that does not permit the classification is skipped for its fallbacks and the call is refused as `assistant-model-unavailable`. There is no save-time or startup check that a skill's profile permits its classification. | Fallback profiles may permit it, so only the resolution knows; this is the check consultations rely on. | For Lars to review |
| C3 | A refused classification fails like an unavailable model, which the run treats as transient and retries up to the agent's `Retry`; a dedicated non-transient failure code is left open. | Changing how the shared resolver reports a denial reaches beyond the agent runs. | For Lars to review |
| C4 | The classification also classifies the skill's instructions asset and the run's hand-off tool calls; a consultation's own `DataClassification(...)` keeps classifying its answers in the asking conversation and is unchanged. | The hand-off message is the run's data; the consultation setting has its own meaning. | For Lars to review |
| C5 | A data skill stores its classification as a name (`NhAssistantDataClassificationNames`, nullable, migration `SkillDataClassification`); the run settings of agents created in the administration get none. | The request names data skills; an administration agent's skills carry the classification. | For Lars to review |
| C6 | No sample case for the classification and the administration profile: the sample's profiles permit `Internal` only, and the branch's sample evidence satisfies the gate; library tests cover both providers. | "Sample evidence where the gate needs it". | For Lars to review |
| P1 | An agent created in the administration chooses its profile in its definition (`profile` on the agent input), stored in the existing `ProfileName`; not in its run settings. | The profile serves people's chat with the agent as well as its runs, and the column already exists. | For Lars to review |
| P2 | The choosable profiles are exactly the run catalog's `Profiles` (registered, with chat, function calling and streaming), the set data skills choose from; any other name is `profile: not-found`. | The administration may never pick an arbitrary or unconfigured model, and one rule serves skills and agents. | For Lars to review |
| P3 | Omitted on create uses the application's chat profile; omitted on update keeps the agent's profile; agents in code ignore it, as they ignore what they work on. | The same "omitted keeps" rule as the other administration-only fields. | For Lars to review |
