# Agent Roles Roadmap

This document is the roadmap for agents that act as a broad role: one agent that receives many
kinds of events, has many tools and decides by itself what each event needs, while staying cheap
enough to run on every event. It extends the
[autonomous agent runs plan](ai-autonomous-agent-runs-plan.md) and the
[rule runs plan](ai-agent-rule-runs-plan.md). It is a plan, not usage documentation.

The roadmap answers two rounds of consumer feedback. The consumer's concept is an agent that works
like the assistant: an event arrives ("a new message from an external planning system"), the
model reads its instructions, looks at the tools it has, decides what has to happen and proposes
it. That concept is how agent runs already work; the feedback is about breadth and cost. This
document records what the code does today, what already answers the feedback, and the phases that
close the rest. Each phase has its own plan. It was checked against `main` at `9ab24f20` on
2026-10-15.

## Status

Decided on 2026-10-15; nothing is implemented yet. The phase plans hold the decisions and the
answered questions; the phases are built in the order A1, A2, B, C, D.

| Phase | Plan | Sample case |
|---|---|---|
| A1 | [Agent control](ai-agent-control-plan.md) | SPM-284 |
| A2 | [Subject briefings and run budgets](ai-agent-briefing-plan.md) | SPM-285 |
| B | [Agent skills](ai-agent-skills-plan.md) | SPM-286 |
| C | [Hand-offs between agents](ai-agent-handoff-plan.md) | SPM-287 |
| D | [Agent evaluation](ai-agent-evaluation-plan.md) | SPM-288 |

## The role, end to end

1. **Filter.** A trigger decides whether an event concerns the role at all (`When`), before
   anything is queued (A1).
2. **Coalesce.** Events about the same subject join one run (`OnBusy(Coalesce, debounce)`,
   existing).
3. **Briefing.** The application supplies a bounded sketch of the subject, including the signals
   its rules found (A2).
4. **Triage.** Rules, which cost nothing, or a cheap model decide whether there is anything to do
   and which skill does it (rules exist; skill routing is B).
5. **Skill run.** A strong model works with only that skill's instructions and tools, under a
   token ceiling per run and the skill's own budget (A2, B).
6. **Proposals.** Guards, deduplication, rejection cooldown, approval and the inbox (existing).
7. **Hand-off.** The role asks another role to act on a subject, under that role's own identity
   and governance (C).
8. **Control and evaluation.** Pause (events wait) and stop (work ends) per agent and skill from
   code and the administration (A1); compare a variant against live work before it goes live (D).

## What already answers the feedback

These exist in nuget-common 11.0.0 and later; a consumer should check that it uses them before
anything else is built:

- **Rules first, the model only when needed.** `UseRuleHandler<T>()` runs headless runs without a
  model; `NhAssistantRuleResult.HandOver(reason)` gives an unclear case to the model in the same
  run. This is free triage.
- **Coalescing per subject.** `OnBusy(NhAssistantTriggerConflict.Coalesce, debounce)` turns a burst
  of events about one subject into one run. It is the strongest cost lever.
- **Continuity.** Every run starts with the thread's working memory, recent run summaries,
  rejections with their feedback and proposals that people overtook.
- **No duplicate work.** Identical proposals are recorded once, a newer proposal for the same
  objects replaces the open one, and rejections hold for a cooldown.
- **Shadow mode.** A second agent with `Shadow()` on the same triggers runs alongside on real
  events without notifying anyone or executing anything.
- **Transactions.** Triggers published inside the application's transaction fire after the commit.

## Assessment of the feedback

### Verified claims

| Claim | Verified | Where |
|---|---|---|
| The model only gets the trigger message, a small payload and short facts | Partly: also the continuity block. Facts are at most 20 × 300 characters and describe the user and the moment, not the subject. | `INhAssistantTurnContextProvider`, `NhAssistantSubjectMemory` |
| Budget, enabled flag, instructions and tools exist per agent only | Yes; per trigger there are only `UseModel`, `Payload`, conflict, debounce and approval policy | `NhAssistantAgentRunOptions`, `NhAssistantTriggerDefinition` |
| The model profile is set per agent | Yes; `UseModel()` per trigger only chooses between rules and the agent's model | `NhAssistantAgentDefinition.ProfileName` |
| No token ceiling per run | Yes; the profile bounds each call and the number of calls, the daily budget is checked at run start | `NhAiModelBudget`, `NhAssistantAgentBudget` |
| A headless agent cannot ask another agent | Yes; consultation tools are not offered, the gate denies them and the handler requires a person | `NhAiAgentInvocation`, `NhAssistantConsultationTools` |
| The kill switch has no C# API, the administration toggle is not the kill switch, reset turns an agent on | Yes, and the code flag `Enabled(false)` and the database flag do not know each other | A1 |
| No filter before queuing, schedules query while the agent is off | Yes; disabled agents still evaluate the trigger's lambdas | A1 |
| The Agent Framework library offers sub-agents and workflows outside the inbox | No: it offers the adapter every assistant turn already runs on and a checkpoint reference adapter; a host composing its own workflows gets governed tools but no proposals | `NhAiAgentFrameworkAdapter` |

### What this roadmap does differently, and why

- **Skills are part of an agent, not a second kind of agent.** A skill has its own description,
  instructions, tools, model, budget, switch and triggers, but shares the agent's identity, thread,
  memory and approval policies. Separate agents remain the answer for separate roles (B).
- **Hand-offs, not consultations, between agents.** A consultation returns content to the asker;
  between agents that would move results read with one agent's grant into a thread that other
  people read. A hand-off starts work in the other agent's own thread under its own governance and
  returns the target's summary only when both threads have the same readers and the target read
  nothing restricted (C).
- **No second orchestration world.** Agent runs stay the governed unit. If explicit multi-step
  workflows are needed later, an Agent Framework workflow can become a run handler of an agent,
  like rules, so its tool calls still become proposals. No case needs it yet.
- **Measure with shadow agents first, replay later.** Parallel shadow agents exist; a comparison
  report makes them useful. Replaying last week's inputs evaluates against today's subject state,
  which the report has to say (D).

### Cost

Model cost is runs × model calls per run × tokens per call × price per token. Each phase lowers
one factor:

| Factor | Lever |
|---|---|
| Runs | `When` filters (A1), coalescing (existing), rules and model triage (existing, B) |
| Model calls per run | Briefings replace exploratory tool calls (A2) |
| Tokens per call | Skills offer only the tools and instructions a task needs (B) |
| Expensive runs | Triage with rules or a cheap model before a strong model (B) |
| Runaway runs | A token ceiling per run (A2) and budgets per skill (B) |

The prompt already places the stable instructions before the turn's data, so providers with
automatic prefix caching reuse them. Explicit cache markers for providers that need them depend on
provider support in Microsoft.Extensions.AI and stay out of scope until a provider offers them.

## Order and dependencies

- A1 first: it fixes defects, and B and C reuse its states (active, paused, stopped) for skills
  and hand-off targets.
- A2 next: briefings and run ceilings help every agent, including those without skills.
- B builds on A1 (switch per skill) and A2 (ceiling and briefing per skill).
- C builds on A1 (a paused target refuses hand-offs).
- D can start after A1 and gains from B (comparison per skill).

Each phase is a minor release of nuget-common and, where the client changes, npm-ai-chat, with its
own sample case in SampleProjectManagement. The sample grows one role through the phases: a
project planner that triages task and deadline events by rule, works on deadline risks with a
model skill, hands completed work to the project closer and is compared against a shadow variant.
