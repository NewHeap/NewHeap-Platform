# Agent Skills Plan

This document is the architecture and implementation plan for skills: parts of one agent that each
have their own description, instructions, tools, model, budget, state and triggers, so one agent
can act as a broad role without every call carrying every tool. It is phase B of the
[agent roles roadmap](ai-agent-roles-plan.md) and builds on
[agent control](ai-agent-control-plan.md) and [briefings and run budgets](ai-agent-briefing-plan.md).
It was checked against `main` at `9ab24f20` on 2026-10-15.

## Status

Implemented on 2026-10-15, with sample case SPM-286. The choices are part of the
[decision record](#decision-record) and the [answered questions](#answered-questions). The consumer
guide `ai-agent-runs` and the sample are authoritative from now on; the implementation deviates from
the plan where [Implementation notes](#implementation-notes) says so.

## Implementation notes

- **Routing order in practice.** A trigger's skill wins. Without one, the agent's rule handler runs
  first; `HandOver` means the agent's own model (no triage), `UseSkill` routes. Model triage runs
  only when neither decided, not for request turns, and only offers active skills. A skill with
  rules runs them after routing; its `UseSkill` may route once more to a model skill. Its
  `HandOver` first continued in the agent's own model; since the next release it continues in the
  same skill (see below).
- **Rules and a model in one skill (later change).** A skill may have rules and a profile. When
  its rules hand over, the run stays in the skill: its tools, instructions, profile (or else the
  agent's), ceiling, budget and state. Leaving the skill gave the model every tool of the agent and
  the agent's ceiling, against the idea of a skill.
- **Follow-up runs (later change).** The follow-up of a decided or executed proposal continues in
  the skill of the run that made it while the agent declares it, recorded as chosen by
  `follow-up`; a paused skill lets it wait and a stopped one refuses it.
- **Triage** is one tool-free call to the triage profile with the briefing, the run's events and the
  catalog, asking for `{"skill": ..., "reason": ...}`; structured output is requested when the
  profile declares it. An answer that is not usable leaves the run without a skill. "Nothing to do"
  ends the run completed with the reason as the agent's message, which becomes the run's summary.
  A triage profile needs chat only.
- **`use_skill`** is an internal function next to the working memory. Calling it ends the model loop;
  the same turn continues with the chosen skill's agent, the messages so far and a system note.
  After two switches it is no longer offered. MCP tools assigned to the agent stay available in
  every skill.
- **Skill states** have the A1 meanings for triggers that name the skill. Rules that route to a
  paused skill give the run's inputs back as waiting inputs of that skill and end the run cancelled
  with `assistant-skill-paused`; a stopped skill ends it with `assistant-skill-stopped`. A stopped
  skill refuses its triggers before their filter runs, so every publish of their event type is
  answered and audited once.
- **Inputs and coalescing.** Inputs carry the skill their trigger named; an event joins only a
  queued run of the same skill, a run claims inputs without a skill and those of its own skill, and
  a resume of a skill starts runs for its waiting inputs only.
- **Budgets.** A run its trigger put in a skill checks the skill's daily budget at the start; rules,
  triage and `use_skill` check it when they choose the skill. Usage per skill is the sum of the runs
  that ended in it; the administration shows today's usage across scopes, while budgets apply per
  scope.
- **Held executions** are not tied to skills: stopping a skill cancels its runs and drops its waiting
  events but leaves approved proposals to execute.
- **The sample** introduces the project planner here, with a task follow-up skill by rules and a
  deadline-risk skill with triage on a daily deadline check. Because a schedule tick runs once per
  minute, the sample's second check is a manual start by the approver.

## Problem

- **One agent, one configuration.** Instructions, tool selectors and the model profile belong to
  the agent definition; budget, switch, retry and grants to its run options. Per trigger there are
  only `UseModel`, `Payload`, conflict, debounce and the approval policy.
- **Every call carries every tool.** A role with dozens of tools sends all their schemas with every
  model call. That costs tokens on every call and makes the model choose worse.
- **One model for everything.** "Is there anything to do?" and "work out the deadline risk" use the
  same profile; `UseModel()` only chooses between rules and that profile.
- **One budget and one switch.** An expensive model task and a cheap rule task of the same role
  cannot be budgeted or switched off separately, so applications split one role into several agents
  with separate threads, or build the composition themselves.

## Goals

- An agent declares skills; each skill can narrow tools, add instructions, choose a model profile
  or rules, and has its own daily budget, run ceiling, state, triggers and approval policy.
- A run works in one skill at a time and sees only that skill's tools.
- A cheap step decides which skill an event needs, or that nothing is to be done.
- Agents without skills behave exactly as today.

## Non-goals

- Skills with their own identity, grants, memory or thread; those are agents.
- Running skills of one run in parallel.
- Loading single tools on demand; the unit of loading is a skill.

## Design

### Declaring skills

```csharp
assistant.ConfigureAgent("project-planner", agent => agent
    .UseRuleHandler<PlannerRules>()                          // optional agent-level triage by rules
    .Triage(triage => triage.UseProfile("planner-triage"))   // optional model triage
    .Skill("task-follow-up", skill => skill
        .Description("Follows up completed and overdue tasks with new tasks or reminders.")
        .UseRuleHandler<TaskFollowUpRules>()
        .Budget(NhAssistantAgentBudget.PerDay(toolCalls: 200, tokens: 0)))
    .Skill("deadline-risk", skill => skill
        .Description("Judges whether a project will miss its deadline and proposes a status change.")
        .UseProfile("planner-strong")
        .Instructions(PlannerAssets.DeadlineRiskInstructions)
        .Tools(ProjectAiTools.ChangeStatusToolId, "projects.search")
        .MaxTokensPerRun(40_000)
        .Budget(NhAssistantAgentBudget.PerDay(toolCalls: 100, tokens: 400_000))
        .UseApprovalPolicy("project-review")));

assistant.AddTrigger<TaskChanged>("task-changed", trigger => trigger
    .StartAgent("project-planner")
    .Skill("task-follow-up")
    ...);
```

- **Identity and validation:** skill ids are dash-case and unique per agent; at most 20 skills per
  agent; a description of at most 300 characters. Skill tool selectors must be covered by the
  agent's selectors, a skill's profile must exist with the capabilities the agent needs, and a
  skill uses either rules or a model. Startup validation fails otherwise.
- **What a skill inherits:** the agent's identity, grants, accountability, thread, memory, briefing,
  continuity and approval policy unless the skill sets its own.
- **What a skill narrows or adds:** tools (intersection with the agent's selectors and grants),
  instructions (appended to the agent's instructions), profile, rule handler, budget, run ceiling,
  approval policy.

### Choosing the skill of a run

In order; the first that decides wins:

1. **The trigger** names a skill with `.Skill(id)`.
2. **The agent's rule handler** returns `NhAssistantRuleResult.UseSkill(id, reason)` next to `Done`
   and `HandOver`.
3. **Model triage**, when the agent has `Triage(...)`: one call to the triage profile with the
   briefing, the run's inputs and the catalog of active skills (id and description), answering with
   structured output: nothing to do, with a reason, or a skill with a reason. Nothing to do ends the
   run as completed with that reason as its summary.
4. **No skill:** the agent's own instructions, tools and profile, as today.

The run records the skill and who chose it (`trigger`, `rules`, `triage`).

### Working in a skill

- The run's turn creates the agent with the skill's tools, instructions and profile; the model sees
  no other tools.
- **Switching skills:** in an agent with skills, the model gets the internal function
  `use_skill(skill, reason)` and the catalog of the other active skills. Calling it ends the current
  model loop and continues the same run with the chosen skill's agent, keeping the messages so far;
  at most two switches per run. Budget and ceiling follow the skill that works.
- **People in the thread:** their turns start without a skill, with the agent's own tools, plus the
  catalog and `use_skill`, so a person's question loads only the skill it needs.
- **Rules in a skill:** a skill with a rule handler runs rules; `HandOver` continues in the same
  skill's model (changed later; first in the agent's default model) or with `UseSkill` in another
  skill.

### Budgets, ceilings and states

- **Daily budget per skill** next to the agent's: a run of a skill checks both at start; usage is
  recorded per run with its skill and summed per skill, agent and scope key per day.
- **Run ceiling:** the skill's `MaxTokensPerRun`, otherwise the agent's.
- **State per skill:** `INhAssistantAgentControl` pauses, stops and resumes a skill
  (`PauseAsync(agentId, actorId, reason, skillId)` and so on), stored per agent and skill with who,
  when and why, with the meanings of phase A1: a paused skill lets its events wait, a stopped skill
  refuses them and cancels its active runs. Triage and `use_skill` offer only active skills. The
  agent's state covers all its skills.

### Persistence and contracts

- Migration `AgentSkills`: `AssistantRun.SkillId` and `SkillChosenBy`; table
  `AssistantAgentSkillState` (agent, skill, state, changed at, by, reason).
- `NhAssistantRunDto.Skill` and `SkillChosenBy`; the agent state of `INhAssistantAgentControl`
  lists its skills with their states.
- Audit: the skill on `AgentTriggerAccepted` and `AgentRunEnded`; state changes of a skill as
  `AdminAgentPaused`, `AdminAgentRunsStopped` and `AdminAgentRunsResumed` with the skill named.

### Client

- The run timeline shows the skill and who chose it.
- The administration lists an agent's skills with their states and daily usage.

## Decision record

Proposed on 2026-10-15; decided the same day.

| # | Decision | Rationale |
|---|---|---|
| D1 | A skill is part of an agent: own description, instructions, tools, model or rules, budget, ceiling, state, triggers and approval policy; shared identity, grants, thread and memory. | One role, one thread, one memory; separate roles stay separate agents. |
| D2 | One skill at a time per run; the model sees only that skill's tools. | Fewer tokens per call and better choices. |
| D3 | Trigger, rules, model triage, none — in that order. | Deterministic routing is free; the model decides only what rules leave open. |
| D4 | Model triage answers with structured output and may end the run with nothing to do. | A cheap call prevents an expensive one. |
| D5 | `use_skill` switches within a run, at most twice. | Progressive disclosure without losing the run's work. |
| D6 | People's turns start without a skill and can load one. | Questions stay cheap until they need a skill's tools. |
| D7 | Daily budget and state per skill on top of the agent's. | Expensive and cheap work of one role are controlled apart. |
| D8 | Agents without skills are unchanged. | Opt-in; no migration of existing agents. |

## Delivery phases

1. **Declarations and routing.** The skill builder and validation, the trigger's `.Skill`, the rule
   result `UseSkill`, the run's skill, the skill's agent per turn.
2. **Triage and switching.** `Triage(...)` with structured output, `use_skill`, people's turns.
3. **Budgets and states.** Daily budget and ceiling per skill, the state per skill on the control,
   migration `AgentSkills`.
4. **Client, evidence and documentation.** Timeline and administration, SPM-286, the guidance rule
   and consumer guide `ai-agent-runs`, release notes.

## Verification

On SQL Server and PostgreSQL:

- A trigger with a skill runs that skill: its tools only, its instructions after the agent's, its
  profile; the run records the skill and `trigger`.
- Rules route with `UseSkill`; model triage routes or ends with nothing to do, in one call to the
  triage profile; without skills nothing changes.
- `use_skill` continues the same run with the other skill's tools and refuses a third switch and a
  skill that is not active.
- A person's turn starts without a skill and loads one with `use_skill`.
- A skill's daily budget and ceiling stop its runs while other skills of the agent keep working.
- Stopping a skill refuses its triggers before any lambda runs, cancels its active runs and hides
  it from triage; pausing it lets its events wait; the agent's state covers all skills.

Sample case SPM-286: the project planner follows up tasks by rules in one skill and judges deadline
risks with a model in another; model triage sends a scheduled check to the deadline skill or ends
it with nothing to do; stopping the deadline skill keeps the task follow-up working.

## Risks

- **Triage mistakes** send work to the wrong skill or drop it. Triage reasons are recorded, the run
  shows them, and phase D compares triage variants in shadow mode.
- **Configuration complexity.** Skills are opt-in; the guide recommends starting with a trigger per
  skill and adding model triage only for events rules cannot route.

## Answered questions

Answered on 2026-10-15:

- **How do people's turns start in an agent with skills?** Without a skill, with the agent's own
  tools, the catalog of skills and `use_skill`.
- **May model triage end a run with nothing to do?** Yes, with the reason recorded as the run's
  summary.
- **Skills and pause or stop?** A skill has the states of phase A1 as well: a paused skill lets its
  events wait, a stopped skill refuses them and cancels its runs.
