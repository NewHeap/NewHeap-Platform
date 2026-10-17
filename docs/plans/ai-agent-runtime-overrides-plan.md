# Agent Runtime Overrides Plan

This document records the design of two runtime overrides of an agent's code: its shadow mode, per
agent and per skill, and the technical account that answers for its runs. Both change at runtime
like the run state of [agent control](ai-agent-control-plan.md), instead of from configuration and
a deploy. It builds on [agent skills](ai-agent-skills-plan.md) and the shadow variants of
[agent evaluation](ai-agent-evaluation-plan.md). It was checked against `main` at `fb2c2c17` on
2026-10-17.

## Status

Implemented on 2026-10-17, with sample cases SPM-293 and SPM-294. The consumer guide
`ai-agent-runs` and the sample are authoritative from now on.

## Problem

An application evaluates a new agent or skill in shadow and then wants it live, or wants a live
agent back in shadow while it investigates, without a deploy. `Shadow()` was a code flag per agent,
read in about ten places, and skills had none. An agent that answers to a technical account named it
in code only (`ServiceAccount(actorId)`), typically from one global setting, while operators need
another account for one agent.

## Decision record

| # | Decision | Reason |
|---|---|---|
| D1 | The shadow mode is a runtime override per agent and per skill, `true`, `false` or `null` (the code decides). Code keeps the default: `Shadow()` on the agent and, new, on a skill. | Operators change the mode without a deploy; the code still says where an agent starts. |
| D2 | The override works in both directions, so an agent in shadow by code goes live at runtime. A shadow variant (`ShadowOf`) always works in shadow. | Going live is the point of an evaluation; a variant going live would double the live agent's work. |
| D3 | Shadow work goes to a separate shadow thread per agent and subject, with its own memory, visible to administrators only. The thread key gains `Shadow`. | Shadow work in the live thread would show members its output and steer the live runs through the shared memory. |
| D4 | Shadow applies to autonomous work. The live thread, its open proposals and people's chat in it stay; only administrators start an agent in shadow by hand; consultations and requests are refused. | People keep deciding what the agent proposed live; nobody gets an answer from an agent in shadow. |
| D5 | A trigger's skill decides the thread when it fires. A live run that rules or triage route to a skill in shadow ends with `assistant-moved-to-shadow`, and a run in the shadow thread takes its events in that skill. `use_skill` in a live thread does not offer a skill in shadow. | The skill is known only after routing; a model switching mid-turn cannot move its turn. |
| D6 | Runs that started finish in their mode. A run still queued moves to the shadow thread before it starts; waiting events move to the thread of the current mode when they resume. | The same rule as a pause: nothing new starts in the old mode. |
| D7 | Threads of variants and of agents in shadow by code whose mode no administrator changed are marked as shadow threads at startup. | Threads from before shadow threads existed stay with administrators. |
| D8 | The technical account is a runtime override for an agent that answers to one in code; any account that exists and may sign in according to `INhAiPrincipalFactory` is allowed. | The user chose any existing account over a host check or a host resolver; the principal factory is what rebuilds the account for runs anyway. |
| D9 | New runs and the agent's threads answer to the new account; runs that started and their proposals keep the account they were made with. | Approval evidence binds the accountable account of a proposal; changing it afterwards would change who answered for a decision. |
| D10 | Changes are audited without their reason (`AdminAgentShadowChanged`, `AdminAgentServiceAccountChanged`); the reason stays with the override. | As for state changes. |

## Answered questions

- *Who decides which accounts may be technical accounts?* Any existing account that may sign in;
  the administrator is trusted, and the guidance warns that the account lends its rights to
  gateway reads.
- *Where does an agent or skill in shadow work?* In a separate shadow thread.
- *May the runtime switch put an agent in shadow by code live?* Yes, except variants.
