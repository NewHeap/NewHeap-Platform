# Agent Evaluation Plan

This document is the architecture and implementation plan for measuring an agent variant before it
goes live: a shadow variant that receives the same events, a report that compares its proposals,
cost and routing with the live agent, and a replay of recorded events. It is phase D of the
[agent roles roadmap](ai-agent-roles-plan.md). It was checked against `main` at `9ab24f20` on
2026-10-15.

## Status

Decided and ready for implementation; nothing is implemented yet. The choices are part of the
[decision record](#decision-record) and the [answered questions](#answered-questions).

## Problem

- **Models do not decide the same way twice.** Guards, revalidation, deduplication and approvals
  catch mistakes after the fact, but a team that changes instructions, a model profile, skills or
  triage wants to know beforehand whether the agent proposes better, worse or more expensively.
- **Shadow mode exists but answers nothing on its own.** A second agent with `Shadow()` on the same
  triggers records proposals with status `shadow` that notify nobody and never execute. Nothing
  compares them with the live agent's proposals and what people decided about those; the triggers
  must be registered twice; and the comparison only covers the future.
- **Recorded events exist.** Every run keeps its inputs, with the trigger, the text and the
  payload, in `AssistantQueuedInput`, until the thread is deleted.

## Goals

- A shadow variant of an agent receives the live agent's events without duplicate registrations.
- A report compares, per period, what the live agent and the variant proposed, what people decided
  about the live proposals, what each cost and how each routed.
- A variant can be run over recorded events of a past period.

## Non-goals

- Automatic promotion of a variant; people decide from the report.
- Judging free text with a model.
- Replaying against the subject's state of the past; replays run against today's state.

## Design

### Shadow variants

```csharp
assistant.AddAgent(plannerV2Definition);                      // other instructions, profile or skills
assistant.ConfigureAgent("project-planner-v2", agent => agent
    .ShadowOf("project-planner"));                            // implies Shadow()
```

- `ShadowOf` makes every trigger of the live agent also fire for the variant, with the same subject,
  message, dedupe key and payload; the variant can still have triggers of its own.
- The variant keeps everything shadow mode guarantees: its threads are visible to administrators
  only, it notifies nobody, nothing executes, and its usage counts against its own budget.
- Pausing or stopping the live agent does not change the variant's state, and the other way round.

### Comparison report

`GET admin/agents/{id}/evaluation?variant={variantId}&from=&to=` for administrators:

- **Pairing:** runs pair when they handled the same event: same subject, trigger and dedupe key, or
  the same replayed input.
- **Proposals:** a live and a shadow proposal agree when they have the same tool and arguments
  hash, or the same tool and objects; otherwise they count as live-only or shadow-only.
- **Rows per tool:** agreed, live-only and shadow-only counts, and for live proposals what people
  decided: approved, executed, rejected (with feedback categories), stale. The interesting cells are
  "rejected live, not proposed by the variant" and "approved live, missed by the variant".
- **Cost:** runs, model calls, tokens and tool calls per run for both, with averages and the 95th
  percentile.
- **Routing:** for agents with skills, the skill each run chose and who chose it, as a matrix live
  against variant.
- **Content:** tool ids, counts and links to the threads only; no messages or arguments.
- **Export:** JSON Lines for the application's own analysis, audited as
  `AdminAgentEvaluationExported`.

### Replay

`POST admin/agents/{variantId}/replay` with `{ sourceAgentId, from, to, maxRuns }`:

- Allowed for shadow variants only; at most 500 runs per replay and one active replay per variant.
- A background operation re-fires the stored inputs of the source agent's runs in the period into
  the variant, oldest first, with their trigger, text and payload; each replayed run records the
  run it replays (`ReplayOfRunId`), which the report pairs on.
- The variant's budget and run limits apply; a replay stops when they are reached and reports how
  far it got.
- The report says that replayed runs saw today's state of each subject, so a subject that changed
  since can explain a difference.

### Persistence and client

- Migration `AgentEvaluation`: `AssistantRun.ReplayOfRunId`; the replay state per variant.
- The administration gets an evaluation view per agent: choose a variant and a period, read the
  tables, start a replay, export.

## Decision record

Proposed on 2026-10-15; decided the same day.

| # | Decision | Rationale |
|---|---|---|
| D1 | `ShadowOf` mirrors the live agent's triggers to a variant. | No duplicate registrations; the same events by construction. |
| D2 | Proposals agree by tool and arguments hash, or tool and objects. | The identities that deduplication already computes. |
| D3 | The report weighs agreement against what people decided about live proposals. | "Different" only matters relative to approvals and rejections. |
| D4 | The report holds counts, ids and links, no content; export is audited. | Evaluation must not become a way to read conversations. |
| D5 | Replays use recorded inputs, against today's state, for shadow variants only, bounded. | Fast feedback without pretending to reproduce the past. |
| D6 | Usage of variants counts against their own budgets. | Evaluation cost is visible and capped. |

## Delivery phases

1. **Shadow variants.** `ShadowOf`, mirrored triggers, validation.
2. **Report.** Pairing, the comparison queries on both providers, the endpoint and export.
3. **Replay.** The operation, its limits and `ReplayOfRunId`, migration `AgentEvaluation`.
4. **Client, evidence and documentation.** The evaluation view, SPM-288, the guidance rule and
   consumer guide `ai-agent-runs`, release notes.

## Verification

On SQL Server and PostgreSQL:

- A variant with `ShadowOf` receives every event of the live agent once, notifies nobody and
  executes nothing; its own triggers keep working.
- The report pairs runs of the same event, counts agreed, live-only and shadow-only proposals per
  tool with the live decisions, and compares cost and routing; it contains no message or argument
  text.
- A replay re-fires the inputs of the period in order, records the runs it replays, stops at the
  variant's budget, and refuses a live agent as target and a second active replay.

Sample case SPM-288: a variant of the project planner with another triage profile runs as shadow
for the sample's events and over a replay of the recorded ones; the report shows where it agreed
with the live planner, where it would have proposed what people rejected, and what it cost.

## Risks

- **Small numbers mislead.** The report shows counts next to percentages and the period it covers.
- **Replay cost.** Replays spend model tokens; the limit per replay and the variant's budget bound
  them, and the report shows what they cost.

## Answered questions

Answered on 2026-10-15:

- **How does a variant get the live events?** Automatically with `ShadowOf`.
- **Replay?** Yes, bounded to 500 runs per replay, for shadow variants only, against today's state
  of each subject.
