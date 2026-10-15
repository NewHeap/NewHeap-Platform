# Hand-offs Between Agents Plan

This document is the architecture and implementation plan for agents that ask other agents to act
without a person: a hand-off starts work in the other agent's own thread, under its own identity
and governance. It is phase C of the [agent roles roadmap](ai-agent-roles-plan.md) and builds on
[agent control](ai-agent-control-plan.md). It was checked against `main` at `9ab24f20` on
2026-10-15.

## Status

Decided and ready for implementation; nothing is implemented yet. The choices are part of the
[decision record](#decision-record) and the [answered questions](#answered-questions).

## Problem

- **A headless agent cannot involve another agent.** Consultation and request tools exist only
  with a person's rights: they are not offered in a run without one, the agent gate denies them
  (they never allow an agent grant), and the handler refuses without an asker ("A consultation
  needs a person whose rights it uses"). Even with an initiator they need `DelegateFromInitiator()`.
- **The workaround is discouraged.** A tool or rule handler can publish an application event whose
  trigger starts another agent. Nothing records that one agent started another, nothing bounds
  loops, and the guidance lists building agent-to-agent calls by hand under Avoid.
- **Roles need it.** A planner that finds a project ready to close should be able to ask the
  closer to look at it, instead of proposing the closer's work itself.

## Assessment

- **A synchronous consultation with an agent's rights is the wrong tool.** It returns content to
  the asker. The consulted agent may read with its own grant (`ElevatedReads.Restricted` hides such
  results from people without the tool's policy), and its answer would land in the asking agent's
  thread, which other people read. Results would leak across agents and audiences.
- **A hand-off returns only what the same readers may see.** It starts the target's run in the
  target's thread, with the target's grants, approval policies, budget and audit; people see its
  work where they see that agent's work. The source learns that the work was accepted and,
  optionally, how it ended, with the target's summary only when both threads have the same readers
  and the target read nothing restricted.

## Goals

- An agent hands work on a subject to another agent that accepts hand-offs from it, from a model
  turn or from its rules, without a person.
- The target works under its own identity and governance; nothing executes without its approvals.
- Every hand-off is traceable, bounded and loop-free.

## Non-goals

- Returning the target's results to the source beyond its run summary, or any text across
  subjects or after restricted reads.
- Hand-offs to agents that did not opt in, or across tenants.
- Waiting for the target inside the source's run.

## Design

### Opting in

```csharp
assistant.ConfigureAgent("project-closer", agent => agent
    .AcceptHandOffs(ProjectSubjects.Type, handOff => handOff
        .From("project-planner")                             // agents that may hand off
        .Description("Checks whether a project can be completed and proposes it.")
        .NotifySource()                                      // a follow-up to the source when the run ends
        .Limits(maxPerSubjectPerDay: 5)));
```

- Both sides opt in: the target lists its sources; the source selects the generated tool
  `assistant.handoff.{agentId}.{subjectType}` in its `ToolSelectors`, like consultation tools.
- The hand-off tool allows an agent grant only for the listed sources, so it works in headless
  runs; people never call it.

### Handing off

- **From a model turn:** the tool takes the subject id and a message of at most 2,000 characters.
- **From rules:** `NhAssistantRuleContext.HandOffAsync(targetAgentId, subject, message, payload)`.
- **Checks, in order:** the target is not stopped (a paused target lets the hand-off wait, like
  other events, phase A1), accepts the source for that subject type, the subject resolves under the
  same scope key as the source's subject (no hand-off across tenants or divisions), the source run
  is not a shadow run handing off to a live agent, the chain allows it, and the per-pair limit is
  not reached.
- **The target's run** fires as trigger `agent-handoff` with no initiator: the target's own grants
  and accountability apply, `DelegateFromInitiator` cannot apply, and its proposals go through its
  own approval policies and the inbox. The message reaches it as untrusted data, introduced with
  the source agent's name; a payload as with event triggers.
- **Receipt to the source:** accepted, deduplicated or refused with a code.
- **Follow-up to the source:** with `NotifySource()`, the source gets a follow-up input when the
  target's run ends, with the target, the subject, the outcome and the number of proposals. It also
  carries the target's run summary, as data from another agent, when both conditions hold:
  - source and target worked on the same subject, so the same people read both threads;
  - the target's run used no restricted results of its own grant (results that people without the
    tool's policy may not see).

  Otherwise the follow-up carries no text. The run records whether it used restricted results.

### Chains and limits

- A run records who handed it off: `RequestedByAgentId`, `RequestedByRunId` and the chain depth.
- `NhAssistantLimits.MaxHandOffDepth`, 2 by default: a chain of hand-offs ends there, and an agent
  already in the chain is never handed work again.
- Per source, target and subject a daily limit; the target's own daily budget and run limits apply.
- Deduplication per source run, target and subject: a model that calls the tool twice in one run
  hands off once.

### Persistence and audit

- Migration `AgentHandOffs`: the three run columns.
- Audit `AgentHandedOff` (source agent and run, target agent, subject, outcome) without the
  message or summary; the target's run audits as any run with its trigger `agent-handoff`.
- `AssistantRun.UsedRestrictedResults`, set when a tool result of the agent's own grant was
  restricted for some readers.
- The run timeline of the target shows "Handed off by {agent}" with a link to the source thread
  when the reader may see it.

## Decision record

Proposed on 2026-10-15; decided the same day.

| # | Decision | Rationale |
|---|---|---|
| D1 | Hand-offs, not agent-rights consultations. | Work moves between agents; content does not move to other audiences. |
| D2 | Both sides opt in: the target lists sources, the source selects the tool. | The same two-key model as consultations and grants. |
| D3 | The target runs under its own identity, grants and policies, with no initiator. | Delegation never widens or borrows rights. |
| D4 | Same scope key only. | No hand-offs across tenants or divisions. |
| D5 | Chains of at most two hand-offs, never back to an agent in the chain; limits per pair and subject. | No loops, bounded fan-out. |
| D6 | Optional follow-up to the source, with the target's run summary only for the same subject and without restricted reads. | The source continues with what the target did, without moving content to other readers. |
| D7 | Rule handlers hand off through the rule context. | Rules signal and route without a model. |

## Delivery phases

1. **Library.** `AcceptHandOffs`, the generated tool, the agent grant for listed sources, the rule
   context method, the checks, chains and limits, the follow-up with its summary rule, migration
   `AgentHandOffs` (the three run columns and `UsedRestrictedResults`), audit.
2. **Client.** The hand-off line in the run timeline and its translations.
3. **Evidence and documentation.** SPM-287, the guidance rule and consumer guide `ai-agent-runs`,
   release notes.

## Verification

On SQL Server and PostgreSQL:

- A headless model run and a rule run each hand a subject to the target, which runs with its own
  grants and records its proposals in its own thread under its own policies.
- A hand-off to a paused target waits and runs after it resumes.
- An agent not listed, a stopped target, another scope key, a shadow source to a live
  target, a third link in a chain and an agent already in the chain are refused with their codes.
- Two calls in one run hand off once; the daily limit per pair and subject holds.
- With `NotifySource()`, the source receives a follow-up with outcome and proposal count, and the
  target's run summary only when both worked on the same subject and the target used no restricted
  results; a hand-off to another subject or after a restricted read carries no text.
- The audit names source and target without the message.

Sample case SPM-287: the project planner finds by rule that all tasks of a project are done and
hands the project to the project closer, which proposes to complete it in its own thread; the
planner receives the closer's summary because both work on the same project.

## Risks

- **Prompt injection across agents.** The source's message is untrusted data for the target, like
  an event message; the target's tools still need approval for effects.
- **Hidden fan-out.** Limits per pair and chain depth bound it; the audit shows every hand-off.

## Answered questions

Answered on 2026-10-15:

- **What does the source get back?** With `NotifySource()`, the outcome and proposal count, and the
  target's run summary when both work on the same subject and the target used no restricted
  results; otherwise no text.
- **How long may a chain be?** At most two hand-offs; an agent already in the chain never gets work
  from it again.
