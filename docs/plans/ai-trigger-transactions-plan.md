# Agent Triggers Inside the Application's Transaction Plan

This document is the architecture and implementation plan for publishing agent triggers inside
the application's own database transaction. It extends the
[autonomous agent runs plan](ai-autonomous-agent-runs-plan.md). It is a plan, not usage
documentation: once the fix exists, the consumer guide `ai-agent-runs` and the sample cases are
authoritative.

The plan answers a problem report from a consumer that publishes
`INhAssistantAgentTriggers.PublishAsync` inside the transaction of the domain change, right before
the commit, so that a run belongs to exactly the change that committed and no event is lost when
the process stops after the commit. This plan treats the report as a description of a defect,
verifies it against the code and chooses its own solution; see
[Assessment](#assessment-of-the-report). It was checked against `main` at `d5159cbf`
(nuget-common 11.0.1) on 2026-10-15.

## Status

Implemented on 2026-10-15, with sample case SPM-281 extended. The choices are part of the
[decision record](#decision-record). The consumer guide `ai-agent-runs` and the sample are
authoritative from now on; the implementation deviates from the plan where
[Implementation notes](#implementation-notes) says so.

## Implementation notes

- **Only the application's entry points defer.** `PublishAsync` and `StartAsync` defer inside a
  transaction. The assistant's own endpoints (`threads/start`, restart), queued messages,
  consultation requests, schedules, webhooks and follow-ups fire at once, because their callers
  need the run and its thread even when an application wraps every request in a transaction.
- **No ambient transactions.** The background operations cannot enqueue inside a
  `TransactionScope`: EF Core refuses to begin their transaction there. That was true before this
  change as well, so only the transaction on the background operations' context is recognized.
- **The client is unchanged.** Its receipts come from the assistant's endpoints, which never defer.
- **A deferred fire that is rejected** (agent disabled, subject gone, rate limit) succeeds as an
  operation; the rejection is audited like any other trigger outcome.
- **Orphan recovery** enqueues a new run operation with the run's original idempotency key, owner,
  division and concurrency key; when that is not possible the run is cancelled as before.

## Problem

`PublishAsync` writes the agent thread, the run and the waiting input through
`NhAssistantRunStore`, which uses its own `nhai` context. Only the background operation of the run
lives in the application's context (`nhops`) and rolls back with the application's transaction.
After a rollback:

1. **An orphaned run** stays `Queued` without an operation until
   `NhAssistantRunMaintenance.CancelOrphanedRunsAsync` cancels it after ten minutes.
2. **A committed change coalesces into the dead run.** The `Coalesce` branch only asks whether a
   run is queued, not whether its operation exists. Cancelling the orphan does not touch the
   inputs, so the committed input keeps waiting and no run starts for it.
3. **A phantom input.** The rolled-back input waits too; the next run of the thread claims every
   waiting input and receives an event for a change that never happened.
4. **A retried transaction loses its event.** `FindRunByEventKeyAsync` skips an orphan only after
   maintenance marked it. A retry with the same `Dedupe` key within ten minutes, from an execution
   strategy or a person saving again, is answered `deduplicated` and starts nothing. The report did
   not mention this one.

The interface documentation of `INhAssistantAgentTriggers` recommends publishing inside the
transaction ("the background operation joins the transaction, so a rollback never starts a
run"), the consumer guide allows it with "run rows of a rolled-back transaction are cleaned up
later", and sample case SPM-281 does it. None of them mentions the consequences above.

## Assessment of the report

### Verified claims

| Claim | Verified | Where |
|---|---|---|
| Run and input are written outside the application's transaction | Yes; every `NhAssistantRunStore` method creates its own context | `AI.Chat/Persistence/NhAssistantRunStore.cs` |
| An orphan is cancelled after ten minutes | Yes | `AgentRuns/NhAssistantRunMaintenance.cs` `CancelOrphanedRunsAsync` |
| `Coalesce` joins a queued run without checking its operation | Yes | `AgentRuns/NhAssistantAgentTriggers.cs` `FireCoreAsync` |
| The next run claims every waiting input of the thread | Yes | `NhAssistantRunStore.ClaimQueuedInputsAsync` |
| Declarative revalidation already behaves | Yes; it is a background operation that runs after the commit | `NhAssistantAgentTriggers.QueueRevalidationsAsync` |

### What the report gets right

- Publishing inside the transaction is a legitimate pattern: it is the only way the application
  gets "the run exists if and only if the change committed" without an outbox of its own.
- A rollback must leave nothing behind, and a committed publication must always lead to a run.
- Deduplication, the conflict behaviors, debounce, revalidation and publishing after the commit
  must behave as today.

### What this plan does differently, and why

- **Neither A (join the transaction) nor B (commit markers), but deferring.** Inside the
  application's transaction the platform writes nothing to `nhai`. It records the publication as one
  background operation in the application's context, which commits or rolls back with the change.
  After the commit the operation fires the trigger through the unchanged path. This is the
  mechanism declarative revalidation already uses; the background operations are the outbox.
- **Why not A.** It works only when `nhai` lives in the same database and shares the connection,
  which the assistant does not require (it has its own connection string). Every run-store method
  would have to take that connection. A long application transaction would hold locks on runs and
  inputs that run workers and maintenance read, with blocking and deadlocks on SQL Server, and
  coalescing would decide on uncommitted rows.
- **Why not B.** Markers per input, coalescing only into marked runs and claiming only marked
  inputs rebuild an outbox; the background operation already is the marker.
- **The receipt inside a transaction says `deferred`.** It cannot name a run that does not exist
  yet; the run, its thread and the outcome follow after the commit and are audited as today.

### What the report missed

- The lost retry (problem 4).
- The thread row: a rollback also leaves an empty agent thread today.
- A process that stops between writing a run and enqueuing its operation leaves an orphan with a
  committed input even outside a transaction; maintenance should queue it again instead of
  cancelling it.

## Goals

- A rollback of the application's transaction leaves no thread, run, input or audit event behind.
- A committed publication always leads to a run, or joins a run that itself is committed, within
  the normal dispatch time.
- Publishing after the commit, webhooks, schedules, follow-ups and revalidation behave as today.

## Non-goals

- Enlisting `nhai` in the application's transaction.
- Detecting a transaction on a DbContext that does not hold the background operations; the
  application publishes inside the transaction of the context that does.
- Changing how runs claim inputs or how coalescing works after the commit.

## Baseline

Paths are relative to `src/Back-end/Libraries`.

| Area | Exists today | Change |
|---|---|---|
| Publishing | `PublishAsync` and `StartAsync` call `FireAsync`, which writes thread, run and input and enqueues the run operation | Inside a transaction: enqueue one `nh-ai-fire-trigger` operation per fire and return `deferred` |
| Operations | `nh-ai-agent-run`, execute-proposal and revalidation operations | `NhAssistantFireTriggerRequest` with its handler |
| Inputs | `AssistantQueuedInput` with a random id | The deferred fire carries the input id, so a retried operation fires once |
| Maintenance | Cancels a queued run without an operation after ten minutes | Queues it again with a new operation when its thread, agent and subject still exist |
| Receipts | `started`, `queued`, `coalesced`, `superseded`, `ignored`, `deduplicated`, `rejected` | `deferred` |

## Design

### Recognizing the application's transaction

`NhAssistantAgentTriggers` treats a fire as inside the application's transaction when the context of
the background operations (`IRepository<NhBackgroundOperation>.Context`) has a current transaction,
or an ambient `System.Transactions.Transaction` exists. That is the transaction the run operation
would have joined. Webhooks, schedules and the deferred operation itself fire immediately.

### Deferring a fire

- `FireAsync` enqueues `NhAssistantFireTriggerRequest` with everything the fire needs: trigger,
  agent, subject, initiator, message (bounded to `MaxMessageChars`), dedupe key, conflict behavior,
  debounce, payload and a new input id.
- The operation is owned by the assistant's system actor, keyed for idempotency by the input id,
  and serialized per agent and subject with a concurrency key, so the fires of one subject run in
  order.
- The receipt reads `deferred` with no run or conversation id. Nothing is written to `nhai` and no
  audit event is raised until the operation runs.
- When the operation cannot be enqueued, the receipt is `rejected` and nothing else happens, as
  when a run operation cannot be enqueued today.

### Firing after the commit

`NhAssistantFireTriggerOperation` fires the request through the existing path, with its input id:
thread, deduplication, rate limit, `Coalesce`, `Queue`, `Supersede`, `Ignore`, debounce, the run's
operation and the trigger audit. When an input with that id already exists, a retried operation
fired before and stops.

### Orphans

`CancelOrphanedRunsAsync` first tries to queue an orphaned run again: when its thread, agent and
subject still exist and the agent is enabled, it enqueues a new run operation with the run's
original owner, division and concurrency key and records it on the run. Otherwise it cancels the
run as today. Orphans now come only from a process that stopped between writing the run and
enqueuing its operation, whose input is committed.

### Documentation

- `INhAssistantAgentTriggers` and the guidance rule `nh-ai-agent-runs` describe both ways: inside
  the transaction (deferred until the commit, `deferred` receipt) and after the commit (immediate
  receipt). The avoid item about rolled-back run rows becomes one about publishing inside a
  transaction on a context that does not hold the background operations.
- Release notes with adoption notes for the `deferred` receipt.

## Decision record

Proposed on 2026-10-15.

| # | Decision | Rationale |
|---|---|---|
| D1 | Inside the application's transaction a fire becomes a background operation that fires after the commit. | Exactly-once per committed change without sharing connections or locks; the revalidation mechanism. |
| D2 | Recognize the transaction on the background operations' context or an ambient transaction. | That is the transaction the run operation joins today. |
| D3 | The receipt inside a transaction is `deferred`, without run or conversation id. | The run does not exist yet; anything else would be a guess. |
| D4 | The deferred fire carries its input id; an existing input stops a retried operation. | A retried operation never fires twice, also without `Dedupe`. |
| D5 | Fires of one agent and subject are serialized by a concurrency key. | They fire in order and coalesce as published after a commit. |
| D6 | Maintenance queues an orphan again when it can, otherwise cancels it. | An orphan's input is committed; it must not wait for the next event. |
| D7 | Additive: one operation type, one outcome, no migration; release notes decide minor or major. | The receipt inside a transaction changes. |

## Delivery phases

1. **Library.** Detection, the deferred fire and its operation, input ids, orphan recovery, the
   `deferred` outcome and the client's receipt type.
2. **Evidence and documentation.** Library tests with real background operations in an
   application context on SQL Server and PostgreSQL, sample case SPM-281 extended, the guidance rule
   and consumer guide `ai-agent-runs`, release notes and this plan's implementation status.

## Verification

On SQL Server and PostgreSQL, with background operations in an application context and a real
application transaction:

1. Publish inside the transaction and roll back: no thread, run, input or operation.
2. Roll back, then publish and commit the same change with the same `Dedupe` within ten minutes:
   one run with only the committed input.
3. Two committed publications with `Coalesce` in one transaction: one run with both inputs.
4. The same `Dedupe` twice after commits: one run.
5. Publishing without a transaction: the immediate receipt and behavior as today.
6. `AddProposalRevalidation` inside the transaction: unchanged.
7. A deferred operation that runs twice fires once.
8. An orphaned queued run without an operation is queued again by maintenance.

Sample case SPM-281: a rolled-back submission leaves nothing in `nhai`; a committed one reads
`deferred` and starts the agent after the commit.

## Risks

- **Latency.** A deferred fire adds one dispatch interval (one second by default) before the run.
- **Receipts.** Applications that read the run from a receipt inside a transaction must look the
  thread up after the commit.
- **Another context.** A transaction on a DbContext without the background operations is not
  recognized; such a publication behaves as today.

## Answered questions

- **Option A or B?** Neither; deferring through the background operations gives the guarantees
  without sharing connections or rebuilding an outbox.
- **Does the application configure anything?** No; detection is automatic. No migration.
- **Which versions?** The next nuget-common and npm-ai-chat releases after 11.0.1 and 0.10.0.
