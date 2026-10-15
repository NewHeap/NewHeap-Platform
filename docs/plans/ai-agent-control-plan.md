# Agent Control Plan

This document is the architecture and implementation plan for controlling agents from code and
from the administration: a pause that lets events wait and a stop that ends work at once, a public
API for both, triggers and schedules that stop early, and the defects around today's kill switch.
It is phase A1 of the [agent roles roadmap](ai-agent-roles-plan.md). It was checked against `main`
at `9ab24f20` on 2026-10-15.

## Status

Decided and ready for implementation; nothing is implemented yet. The choices are part of the
[decision record](#decision-record) and the [answered questions](#answered-questions).

## Problem

Three flags decide whether an agent works, and they do not know each other:

| Flag | Stored | Set by |
|---|---|---|
| `NhAssistantAgentRunOptions.Enabled` | In memory, per process | `ConfigureAgent(...).Enabled(false)` at startup |
| `AssistantAgent.IsEnabled` | Database | Kill switch, the administration's toggle and editor, reset, the first upsert of a code agent |
| `NhAssistantOptions.Enabled` | Configuration | `NewHeap:AI:Assistant:Enabled` |

What follows from that:

- **The kill switch has no C# API.** `NhAssistantKillSwitch` is internal; only
  `POST admin/agents/{id}/kill-switch` reaches it.
- **The administration's toggle is not the kill switch.** It sends the whole definition with
  `PUT admin/agents/{id}`: active runs keep running and pending proposals of active runs stay open.
- **Reset turns an agent on.** `ResetAsync` sets `IsEnabled = true`, which silently releases an
  engaged kill switch without an audit event.
- **The kill switch bumps the definition's version,** so an administrator who is editing the agent
  gets a conflict on save.
- **A code-disabled agent looks enabled.** The administration shows only the database flag; holding
  approved proposals and people's chat turns read only the database flag; the kill switch cannot
  turn a code-disabled agent on, and nothing says so.
- **The global switch only closes endpoints.** Triggers, schedules and maintenance keep working
  while the assistant is off.
- **Disabled agents still do work.** `PublishAsync` evaluates every trigger's subject, initiator,
  message, dedupe and payload lambdas before it checks the agent, and a deferred trigger is enqueued
  without a check. Scheduled triggers run their `Subjects` query whatever the agent's state and
  reject each subject afterwards, one audit event per subject.
- **Triggers have no filter.** An event that does not concern the agent still evaluates its
  lambdas and is audited as a rejected trigger.
- **There is only one kind of off.** An operator who wants an agent to stop starting new work, for
  example during a migration, can only stop everything and lose the events of that period.
- **Held consultation proposals never execute.** Maintenance re-queues approved proposals after a
  release with `GetApprovalsInStatusAsync`, which only returns proposals with a run; consultation
  proposals have none.
- **The re-queue can starve.** That query takes the first 100 approved proposals and filters them in
  memory, so 100 proposals that are already queued for execution hide every proposal behind them.

## Goals

- Two clear states besides active: **paused** (no new runs; events wait and nothing is lost) and
  **stopped** (work ends at once; events are refused), each with a reason, an actor and a time, the
  same from code, from the endpoint and from the administration.
- An agent that is not active evaluates no lambdas and runs no queries it does not need.
- Reset, editing and the state never change each other.
- The global switch stops all agent work.
- Held proposals, including consultation proposals, execute after a stop ends.

## Non-goals

- States per skill; phase B adds them on top of these states.
- Scheduling a state change (paused at night, active in the morning).
- Changing what a code-disabled agent means: it stays off until the next deployment.

## Design

### States

| State | New runs | Events and requests | Running runs | Approved proposals | People in its threads |
|---|---|---|---|---|---|
| Active | Start | Fire | Run | Execute | Chat |
| Paused | Do not start | Wait per subject, deduplicated and coalesced | Finish normally | Execute | Chat |
| Stopped | Do not start | Refused (`assistant-agent-stopped`) | Cancelled; their pending proposals withdrawn | Held until active again | Refused |
| Configured off (`Enabled(false)` in code) | As stopped, permanently until the next deployment | | | | |

- **Storage:** `AssistantAgent` gets `RunState` (`active`, `paused`, `stopped`), `StateChangedAt`,
  `StateChangedBy` (at most 256 characters) and `StateReason` (at most 500). `IsEnabled` stays in
  sync (`false` only when stopped) for existing reads. Changing the state never touches the
  definition's version.
- **Waiting events while paused:** a fire writes its input to the thread as a waiting input and
  starts no run. Inputs carry their trigger's dedupe key (new column `EventKey` on
  `AssistantQueuedInput`), so a redelivered event waits once. Rate limits count runs, not waiting
  inputs. Schedules skip their ticks while paused, since they come back by themselves; event
  triggers, webhooks, manual starts and requests wait.
- **Resuming:** for every thread of the agent with waiting inputs, one run starts with the trigger
  of the oldest waiting input and claims them all, as a coalesced run does. Runs start through the
  normal limits, so a long pause does not start everything at once.
- **Stopping:** what the kill switch does today. Resuming from stopped releases held executions.
- **The definition no longer carries the state.** `PUT admin/agents/{id}` and `POST admin/agents`
  accept `isEnabled` and ignore it for one major version (a new agent starts active); reset keeps
  the state.

### Public API

```csharp
public interface INhAssistantAgentControl
{
    Task<NhAssistantAgentState?> GetAsync(string agentId, CancellationToken cancellationToken = default);
    Task<TaskResult<NhAssistantAgentState>> PauseAsync(string agentId, string actorId, string? reason, CancellationToken cancellationToken = default);
    Task<TaskResult<NhAssistantAgentState>> StopAsync(string agentId, string actorId, string? reason, CancellationToken cancellationToken = default);
    Task<TaskResult<NhAssistantAgentState>> ResumeAsync(string agentId, string actorId, string? reason, CancellationToken cancellationToken = default);
}

public sealed record NhAssistantAgentState(
    string AgentId,
    string RunState,          // active, paused, stopped, configured-off
    bool Active,              // active, configured on and the assistant enabled
    DateTimeOffset? ChangedAt,
    string? ChangedBy,
    string? Reason,
    int WaitingInputs,        // events waiting while paused
    int CancelledRuns);       // runs the last stop cancelled
```

- `ResumeAsync` of a configured-off agent fails with `assistant-agent-configured-off`.
- Audit: `AdminAgentPaused` (new), `AdminAgentRunsStopped` and `AdminAgentRunsResumed`. The
  reason is free text and stays out of the business audit sink, like other free text; the state
  keeps it. Live updates for the agent.
- `POST admin/agents/{id}/kill-switch` keeps its contract: `engaged: true` stops, `false` resumes.
  New: `POST admin/agents/{id}/state` with `{ state, reason }` and `GET admin/agents/{id}/state`;
  the agent list returns the state per agent.

### Early checks

- **`When` on triggers.** `AddTrigger<TEvent>(...).When(evt => ...)` and
  `AddWebhookTrigger(...).When(body => ...)` decide first; an event that does not match does
  nothing and is not audited.
- **State before anything else.** `PublishAsync`, `StartAsync`, deferred triggers, schedules and
  webhooks read the agent's state once per call before they evaluate a lambda, call a resolver or
  queue an operation. Stopped and configured-off agents do nothing more; paused agents evaluate the
  lambdas they need to store a waiting input.
- **The global switch** makes `PublishAsync` and `StartAsync` return `rejected` with
  `assistant-disabled` without work, lets scheduled jobs skip their ticks and stops maintenance from
  queuing executions.
- **A refused trigger** of a stopped agent is audited once per publish call and agent, not once per
  subject.

### Held proposals

- Maintenance selects approved deferred proposals without an execution operation, decided more
  than a minute ago, of run threads and consultations alike, in the database query itself, so
  queued executions never hide later proposals.

### Client (`@newheap/platform-ai-chat/admin`)

- The agent list shows the state with who, when and why, `Off in configuration`, and the waiting
  inputs of a paused agent, with actions to pause, stop and resume that ask for an optional reason.
- The editor no longer has an enabled checkbox; reset says that it keeps the state.

## Decision record

Proposed on 2026-10-15; decided the same day.

| # | Decision | Rationale |
|---|---|---|
| D1 | Two states besides active: paused (events wait, running work finishes, executions continue) and stopped (today's kill switch). | Operators can hold new work without losing events, or end everything at once. |
| D2 | The state has its own columns and never bumps the definition's version. | Changing state and editing must not conflict. |
| D3 | The definition endpoints accept `isEnabled` and ignore it for one major version; reset keeps the state. | Existing clients keep working; editing and resetting never change the state. |
| D4 | `Enabled(false)` in code wins and is reported as configured off. | A deployment decision cannot be overruled at runtime. |
| D5 | Public `INhAssistantAgentControl`; the endpoints and the administration use it. | Applications control agents from their own code. |
| D6 | Waiting inputs are deduplicated by the trigger's dedupe key and resume as one run per thread. | A pause loses nothing and does not multiply work. |
| D7 | Schedules skip ticks while paused or stopped. | Recurring checks come back by themselves; a burst after a pause helps nobody. |
| D8 | `When` on event and webhook triggers; stopped agents evaluate nothing. | Unrelated and stopped events cost nothing. |
| D9 | The global switch stops triggers, schedules and execution queuing. | "Assistant off" must mean off. |
| D10 | Held proposals are found by the database query, consultations included. | Approved work must not be lost or starved. |

## Delivery phases

1. **Library.** Migration `AgentState` (SQL Server and PostgreSQL: the state columns and
   `AssistantQueuedInput.EventKey`), the control service and state, waiting inputs and resume, the
   endpoint changes, `When`, the early checks, the global switch and the maintenance query.
2. **Client.** State, waiting inputs and actions in the agent list; the editor and reset texts.
3. **Evidence and documentation.** SPM-284, the guidance rule and consumer guide `ai-agent-runs`,
   release notes with adoption notes for the ignored `isEnabled` and the new states.

## Verification

On SQL Server and PostgreSQL:

- Pausing lets running runs finish and approved proposals execute; events, webhooks and manual
  starts of that period wait per thread, a redelivered event waits once, schedules skip their ticks;
  resuming starts one run per thread that claims the waiting inputs.
- Stopping cancels active runs, withdraws their pending proposals, holds approved executions,
  refuses events and records who, when and why; resuming releases them, consultation proposals
  included.
- The definition's version is unchanged by a state change; an editor saving afterwards succeeds.
- Reset and `PUT` with `isEnabled` leave the state as it is.
- A code-disabled agent reports `configured-off` and refuses to resume.
- A stopped agent's triggers evaluate no lambda, call no resolver and queue nothing; `When`
  returning false does nothing and audits nothing.
- With the assistant disabled, publishing, scheduling and execution queuing stop.
- With more than 100 proposals queued for execution, a held proposal behind them is queued.

Sample case SPM-284: an operator pauses the project planner from the application's own maintenance
page during an import; task events of that period wait and run as one run per project after
resuming; stopping it cancels a running run and holds an approved proposal until it is resumed.

## Risks

- **Behavior change for the administration.** The editor's checkbox disappears; state changes move
  to the list. Clients that send `isEnabled` with a definition no longer change the agent.
- **Long pauses** collect many waiting inputs; the list shows how many, and resuming starts runs
  within the agent's limits.

## Answered questions

Answered on 2026-10-15:

- **What does off mean?** Two states: pause (events wait, running work finishes, executions
  continue, people can chat) and stop (today's kill switch).
- **Events during a pause?** They wait until the agent resumes, without a maximum age.
- **`isEnabled` in the definition?** Accepted and ignored for one major version.
- **The global switch?** It stops triggers, schedules and execution queuing as well.
