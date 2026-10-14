# Proposal Revalidation Plan

This document is the architecture and implementation plan for closing open agent proposals as
soon as the work they propose was done another way, typically by a person in the application.
It extends the [autonomous agent runs plan](ai-autonomous-agent-runs-plan.md). It is a plan, not
usage documentation: once the features exist, the consumer guide `ai-agent-runs` and the sample
cases are authoritative.

The plan answers consumer feedback. A consumer's key-user demonstration depends on it: a person
ticks off an office task by hand, and the agent's open proposal for that task disappears live
with "already done by <name>". The feedback came as a detailed request. This plan treats that
request as a description of what was missing, verifies its claims against the code, and keeps
what holds up; where the code works differently or a simpler design fits better, it says so in
[Assessment](#assessment-of-the-feedback). It was checked against the code on 2026-10-14.

## Status

Implemented on 2026-10-14, with sample case SPM-282. The open questions of the first draft were
answered the same day and are part of the [decision record](#decision-record). The consumer guide
`ai-agent-runs` and the sample are authoritative from now on; the implementation deviates from the
plan where [Implementation notes](#implementation-notes) says so.

## Implementation notes

- **Declarative revalidation is a background operation.** `INhAssistantAgentTriggers.PublishAsync`
  may run inside the application's transaction, where guards in their own scope would read
  uncommitted or locked rows. It therefore queues `nh-ai-revalidate-proposals`, which joins the
  transaction like a run does and starts once it committed. `INhAssistantProposalRevalidation`
  itself runs at once and expects to be called after the commit.
- **A failing guard** refuses an approval with `503 assistant-proposal-check-failed` and keeps the
  proposal pending. Before a deferred execution it keeps the proposal approved and lets the
  maintenance job queue the execution again, counting an attempt. A waiting inline run cannot keep
  its proposal open, so there the proposal closes as `execution-failed` with that code.
- **Stages.** A person's own chat proposal and an inline proposal execute right after the decision,
  so the chat decision runs the decision and execution guards together, and the resumed inline run
  runs the execution guards again. The executor runs the subject-version precondition first and the
  guards second.
- **Keys** are stored only for proposals that revalidation can close: those of agent runs and of
  consultations. A revalidation by key without a scope key uses `global`, the scope of subjects
  without tenant or division.
- **The guard context** has no subject and no scope key for a proposal in a person's chat outside a
  subject thread.
- **The audit event** `AgentProposalInvalidated` carries the kind and reason as
  `kind:reason` in `ResultCode` and the stage in `ObjectId`. The subject-version check before
  execution now uses it too, with kind `changed`.
- **The report** splits stale proposals by whether they closed after their approval: those count as
  approved and in `Stale`, the others as `Invalidated`.
- **The client** keeps closed cards for proposals that closed as stale, not for proposals that a
  colleague decided, which leave the list as before.
- **The system signal** is `NhBackgroundOperationOwnerSignals`, internal to the platform's packages,
  which signals on behalf of the operation's owner, whether that is the agent or the accountable
  user.

## Problem

An approver can see, and even approve, a proposal for work that already happened. A proposal
leaves `pending` only by a decision, by expiry or through the kill switch. Whether a proposal
still fits is checked once: right before an approved deferred proposal executes, and only when
its approval policy sets `RequireUnchangedSubject`. Nothing checks it while the proposal is open
or when someone approves it, and nothing tells people why a proposal no longer applies.

## Assessment of the feedback

### Verified claims

| Claim | Verified | Where |
|---|---|---|
| `INhAiProposalPrecondition` runs only right before an approved deferred proposal executes, and only with `RequireUnchangedSubject` | Yes. An application's replacement precondition is gated by the same flag. | `NhAssistantProposalExecutor` in `AI.Chat.AspNet/AgentRuns/NhAssistantAgentRunOperations.cs` |
| The default precondition compares subject versions and is registered with `TryAddScoped`, so it can be replaced but not complemented | Yes | `NhAssistantSubjectVersionPrecondition`; `NhAssistantServiceCollectionExtensions` |
| `stale` exists only after approval | Yes; only the executor transitions to `stale` | `NhAssistantApprovalStatuses`, executor |
| Both decision endpoints decide without re-checking | Yes; the decision checks hash, expiry and approver rights only | `NhAssistantProposalDecisions` |
| `already-done` exists as a feedback category but nothing sets it | Yes | `NhAssistantProposalFeedbackCategories` |

### What the feedback gets right

- Open proposals must be closable proactively, and an approval must be refused once a proposal
  no longer fits.
- Several checks per tool, from the application, next to the built-in subject-version check.
- Closing must be visible live, explained in each reader's language, audited, and must free a
  waiting inline run.
- The execution-time check must stay, and decision and revalidation must race safely through the
  existing approval transitions.

### What this plan does differently, and why

- **Targets are not the right index.** A proposal's `Targets` are the invocation's execution
  scopes, such as the division or tenant, copied onto every proposal of the turn
  (`NhAssistantToolCallInterceptor`). An office task in the tool's arguments is never a target,
  so revalidating by `TargetsJson` would not find the proposal in the consumer's own example.
  Instead, the application's per-tool guard names the objects a proposal changes ("proposal
  keys", such as `office-task:4711`), and the platform indexes them when the proposal is made.
- **No system feedback.** Writing `already-done` feedback with a system author mixes the
  platform's facts with people's words: the report counts feedback authors and texts, and a
  system author would inflate both. Invalidation is already a fact on the proposal (status
  `stale` with a reason). The report gets invalidation counts per reason instead, and the next
  run's continuity lists invalidated proposals separately from rejections.
- **No new policy flag.** `RevalidateOpenProposals` would guard against every subject edit
  closing every proposal. That only happens with the subject-version check, which stays where it
  is: execution only, behind `RequireUnchangedSubject`. Checks an application registers per tool
  are explicit opt-in, so they apply to open proposals, decisions and execution alike.
- **Revalidation follows the events the application already publishes.** Next to an explicit
  revalidation API, a declarative registration revalidates on the same domain events that start
  agents (`INhAssistantAgentTriggers.PublishAsync`), so an application that publishes after
  commit needs no extra calls.
- **"Already done" is one kind, not the only label.** A guard says what happened: already done,
  changed, or no longer applicable. The client labels each kind; "Already done" is shown only
  when that is what happened.

### What the feedback missed

- **A waiting inline run cannot be woken without a person.** Background-operation signals
  require a user who owns the operation or may signal it; a run owned by an agent has no such
  user when the platform closes its proposal. The platform needs a system signal for its own
  agent operations.
- **Proposals of a person's own chat turn** wait in the conversation stream, not in a run. The
  decision-time check covers them; revalidation leaves them alone, because their author is
  looking at them.
- **Guards fail.** A guard that throws must not close a proposal. It stays pending, the failure
  is logged and counted, and the decision-time check runs again later.
- **Tenancy of keys.** Application ids such as `4711` are not unique across tenants. Keys are
  scoped by the thread's scope key.
- **The live "disappears with a message" moment.** Lists only show pending proposals, so a
  closed proposal would vanish without its explanation. The client keeps a card that just closed
  and shows its explanation until it is dismissed.
- **Bulk decisions** return a result per proposal; stale ones are refused individually.
- **Consultation proposals** (`consultationId`) are proposals like any other and follow the same
  rules.

## Goals

- Applications register per-tool guards that name the objects a proposal changes and check
  whether it still fits.
- Open proposals close as `stale` when the application revalidates a subject or an object, or
  when a registered domain event arrives.
- Approving a proposal that no longer fits is refused and closes it.
- People read why, in their own language, wherever proposals appear, and a waiting run continues.
- The next run knows what was invalidated, and the report counts it without calling it a
  mistake.

## Non-goals

- Revalidating on reads. Lists and subject work stay pure queries; correctness comes from the
  decision-time and execution-time checks.
- An HTTP endpoint for revalidation. Applications revalidate from their own back end.
- Undoing executed proposals.

## Baseline

Paths are relative to `src/Back-end/Libraries`.

| Area | Exists today | Change |
|---|---|---|
| Precondition contract | `INhAiProposalPrecondition`, `NhAiPreconditionResult` (`Satisfied`, `Stale(code)`), `NhAiProposalPreconditionContext` in `AI.Common/NhAiAgentRunContracts.cs` | Kept; guards are added next to it |
| Execution check | `NhAssistantProposalExecutor` runs the precondition behind `RequireUnchangedSubject` and closes as `stale` | Also runs the guards of the tool |
| Decisions | `NhAssistantProposalDecisions` checks hash, expiry and approver rights, then `TryTransitionApprovalAsync` | Runs the guards before approving |
| Transitions | `NhAssistantRunStore.TryTransitionApprovalAsync` with a concurrency stamp; `CloseWaitingToolCallAsync` maps `stale` to `not-executed` | `pending` → `stale` becomes a valid transition |
| Targets | `AssistantApproval.TargetsJson` holds the execution scopes as labels, not indexed | Unchanged; keys get their own indexed table |
| Presentations | `INhAssistantToolPresenter`, rendered in every supported UI culture (at most eight) into `PresentationsJson` | Same mechanism for explanations |
| Inline runs | A decision wakes the run with `INhBackgroundOperationSignalService.SignalAsync` on behalf of the deciding user | A system signal for agent-owned operations |
| Live updates | `AgentWorkChanged` with reasons `run`, `proposal`, `feedback` to participants and members | Reused with reason `proposal` |
| Feedback and continuity | Report counts statuses, categories and authors; continuity lists recent rejections | Counts and lists invalidations separately |
| Client | Status `stale` reads "Outdated" without explanation | Kind label and explanation everywhere proposals appear |

## Design

### Proposal guards

```csharp
// NewHeap.Platform.AI.Common
public interface INhAiProposalGuard
{
    /// The application objects the proposal changes, such as ("office-task", "4711").
    ValueTask<IReadOnlyList<NhAiProposalKey>> GetKeysAsync(NhAiProposalGuardContext context, CancellationToken cancellationToken);

    /// Whether the proposal still fits now.
    ValueTask<NhAiPreconditionResult> CheckAsync(NhAiProposalGuardContext context, CancellationToken cancellationToken);
}

public sealed record NhAiProposalKey(string Type, string Id);

// NhAiPreconditionResult gains, additively:
public static NhAiPreconditionResult AlreadyDone(string reasonCode, Func<CultureInfo, string?>? explain = null);
public static NhAiPreconditionResult Stale(string reasonCode, Func<CultureInfo, string?>? explain = null); // kind Changed
public NhAiStaleKind Kind { get; }   // Changed, AlreadyDone, NotApplicable
```

```csharp
assistant.AddProposalGuard<OfficeTaskGuard>("office-tasks.complete");

// A check that is too expensive for every revalidation runs only when people decide and before
// execution. Revalidation then never closes these proposals; the decision does.
assistant.AddProposalGuard<ExternalRegisterGuard>(
    ["dsp-orders.assess"],
    NhAiProposalGuardStages.Decision | NhAiProposalGuardStages.Execution);
```

- Guards are registered per tool selector. Several guards may apply to one tool; they run in
  registration order and the first stale result wins.
- A guard runs at every stage by default (`NhAiProposalGuardStages.All`). Its registration can
  limit it to some stages; the documentation warns that a guard without `Revalidation` never
  closes open proposals by itself.
- The guard context holds the proposal id, tool id, exact arguments, subject, scope key and the
  proposal's creation time. Guards run in a service scope without a user, must not change data,
  and must be fast.
- A guard that throws keeps the proposal pending; the failure is logged with the guard type and
  counted.
- `GetKeysAsync` runs once, when the proposal is made; the keys are stored with the proposal.
- `INhAiProposalPrecondition` and the subject-version check stay as they are.

### When guards run

Each stage runs the guards registered for it:

1. **Revalidation**, for open proposals of agent runs and consultations.
2. **Decision**, before an approval in either decision endpoint, including a person's own chat
   turn. A stale result closes the proposal and refuses the decision with
   `assistant-proposal-stale` (HTTP 409); a rejection needs no check.
3. **Execution**, next to the existing precondition, for approved proposals.

All three close through `TryTransitionApprovalAsync`. Exactly one terminal state wins a race; a
decision that won before a revalidation is caught at execution.

### Revalidation

```csharp
public interface INhAssistantProposalRevalidation
{
    Task<NhAssistantRevalidationResult> RevalidateSubjectAsync(NhAiSubjectRef subject, CancellationToken cancellationToken = default);

    Task<NhAssistantRevalidationResult> RevalidateKeysAsync(
        IReadOnlyCollection<NhAiProposalKey> keys,
        string? scopeKey = null,
        CancellationToken cancellationToken = default);
}

assistant.AddProposalRevalidation<OfficeTaskCompleted>(revalidation => revalidation
    .Keys(evt => [new NhAiProposalKey("office-task", evt.TaskId.ToString())])
    .ScopeKey(evt => NhAiSubjectInfo.CreateScopeKey(null, evt.DivisionId)));
```

- The declarative form runs inside `INhAssistantAgentTriggers.PublishAsync`, which applications
  already call after commit for triggers.
- Keys are stored in a new indexed table `AssistantApprovalKeys` (approval id, scope key, key
  type, key id), indexed on scope key, type and id, and deleted with the proposal. An in-memory
  filter over the open proposals of a scope would also work for one subject, but revalidation by
  key does not know the subject and must stay cheap when called after every commit, so the index
  wins.
- A revalidation with no open proposal for its subject or keys is one indexed query. It returns
  the proposals it closed with their reason codes, and repeating it changes nothing.

### Closing as stale

- Status `stale` with the guard's reason code, its kind, and its explanation rendered in every
  supported UI culture of `RequestLocalizationOptions` (at most eight, as for presentations).
  Readers get their own culture.
- The tool call reads `not-executed` (already the case for `stale`).
- `AgentWorkChanged` with reason `proposal` reaches participants and the subject's members.
- A content-free business audit event `AgentProposalInvalidated` with the approval id, tool id,
  reason code, kind and stage (revalidation, decision, execution).
- An inline proposal whose run waits is woken with a platform system signal carrying "not
  executed: already done", as a rejection does.
- Reminders and escalations stop, because the proposal is no longer pending.

### Explanations and learning

- `NhAssistantProposalDto`, the thread's decisions and the subject work expose the kind, reason
  code and explanation of a stale proposal.
- The feedback report counts invalidations per tool, agent version, kind and reason next to
  rejections. People may still give feedback on a stale proposal; nobody else writes feedback.
- The next run's `<agent-continuity>` lists recently invalidated proposals of the subject with
  kind and explanation, next to rejections and with the same limits, so the agent does not
  propose them again unchanged.

### Client (`@newheap/platform-ai-chat`)

- A stale proposal shows its kind ("Already done" / "Al gedaan", "Changed" / "Gewijzigd", "No
  longer needed" / "Niet meer nodig") and its explanation in the proposals list, thread view,
  the panel's approval card and the subject suggestions.
- When `AgentWorkChanged` closes a card that is on screen, the inbox, the subject suggestions and
  the panel keep the card in its closed state with the explanation until it is dismissed; badges
  drop the count at once. There is no "recently closed" section: people who look later find the
  proposal in the thread and the proposals list.
- The three kinds have fixed labels in English and Dutch; the guard's explanation gives the
  details.
- A decision refused with `assistant-proposal-stale` reloads the proposal and shows the
  explanation.

## Decision record

Proposed on 2026-10-14.

| # | Decision | Rationale |
|---|---|---|
| R1 | Per-tool `INhAiProposalGuard` with `GetKeysAsync` and `CheckAsync`, registered with `AddProposalGuard<T>(toolSelectors)`; several per tool, first stale wins. | The application knows which objects a tool changes and when that is done; one type keeps keys and check together. |
| R2 | `INhAiProposalPrecondition` and the subject-version check stay unchanged and execution-only. | No behavior change for existing hosts; no new policy flag. |
| R3 | Proposal keys in an indexed `AssistantApprovalKeys` table, scoped by scope key. | Targets are invocation scopes; revalidation by key must be cheap without knowing the subject. |
| R4 | Guards run at revalidation, decision and execution, all through `TryTransitionApprovalAsync`. | One safe path; a lost race is caught at the next stage. |
| R5 | Explicit revalidation API plus declarative revalidation on domain events published through `INhAssistantAgentTriggers`. | Works for applications with and without an event pipeline. |
| R6 | `NhAiPreconditionResult` gains a kind and an explanation callback; explanations are rendered per supported culture when the proposal closes. | Guards run headless; readers need their own language, as for presentations. |
| R7 | No system feedback; invalidations are counted and listed in their own right. | Feedback stays people's words; the report and continuity get the facts. |
| R8 | A platform-internal system signal wakes agent-owned operations waiting for an inline proposal. | Signals require a user today; a public system signal would be a needless security surface. |
| R9 | Revalidation skips proposals of a person's own chat turn; the decision-time check covers them. | Their author is looking at them, and the turn waits in the conversation stream. |
| R10 | Next minor release of nuget-common and npm-ai-chat; one `nhai` migration for the key table and the stale columns. | Additive only. |
| R11 | The public contract is `INhAiProposalGuard`, a new type next to `INhAiProposalPrecondition`. | Non-breaking, and the name says it does more than the execution-time precondition. |
| R12 | Guards run at every stage by default; a registration may limit them with `NhAiProposalGuardStages` flags (`Revalidation`, `Decision`, `Execution`). | Expensive checks can skip revalidation without giving up the decision and execution checks. |
| R13 | Three fixed kinds: `AlreadyDone`, `Changed` and `NotApplicable`, with fixed client labels. | Covers the consumer's case, the existing subject-version check and the rest, without translations per application. |
| R14 | A proposal that closes while on screen stays as a closed card with its explanation until dismissed; no "recently closed" section. | The live moment without an API change or inbox noise. |

## Delivery phases

1. **Guards and decision-time checks.** The contracts, registration, the guard runner, guards at
   decision and execution, `pending` → `stale`, the refusal code, audit and live update.
2. **Keys and revalidation.** The key table and migration on both providers, keys at proposal
   creation, `INhAssistantProposalRevalidation`, declarative revalidation on events.
3. **Explanations and learning.** Kind and explanation columns, rendering per culture, the DTOs,
   the report counts and the continuity block.
4. **Inline runs.** The system signal in background operations and its use for stale inline
   proposals.
5. **Client.** Kind labels, explanations, the closed-card state and the refused-decision reload.
6. **Evidence and documentation.** Sample case SPM-282, guidance rule and consumer guide
   `ai-agent-runs`, release notes, and the agent-runs plan's implementation status.

## Verification

On SQL Server and PostgreSQL:

- Revalidating a subject closes only proposals whose guard reports stale; a second call changes
  nothing.
- Revalidating by key closes a proposal in another subject's thread that changes that object, and
  never one in another scope.
- A declared event revalidates without an explicit call.
- Approving a stale proposal is refused with `assistant-proposal-stale` and closes it; a decision
  racing a revalidation ends in exactly one terminal state.
- Guards run only for their tools, several combine, and a throwing guard keeps the proposal open.
- A guard limited to `Decision | Execution` leaves open proposals alone at revalidation and still
  refuses the decision.
- A waiting inline run continues with "not executed".
- The explanation reads correctly in every supported culture.
- `AgentWorkChanged` reaches the subject's members, and the audit event carries no content.
- The report counts invalidations separately and never as wrong; the next run's continuity lists
  the invalidated proposal.

Sample case SPM-282: the intake reviewer proposes to complete a project task (a new tool on
`ProjectTask.IsCompleted`) and to activate the project. A project member completes the task by
hand; the task proposal closes live as "already done by <name>", and approving it afterwards is
refused. Setting the status by hand closes the status proposal through subject revalidation.

## Risks

- **Guard cost.** Guards run on every revalidation hit. Mitigation: the key index limits them to
  affected proposals, and guards must be read-only and fast; the documentation says so.
- **False staleness.** A wrong guard closes good proposals. Mitigation: the reason and
  explanation are visible, invalidations are audited and reported per guard reason.
- **System signal.** Waking runs without a user is new. Mitigation: internal to the platform,
  only for agent-owned operations, audited.

## Answered questions

The first draft left four questions open. They were answered on 2026-10-14:

| Question | Answer | Decision |
|---|---|---|
| Public name: guard or an extended precondition | `INhAiProposalGuard` | R11 |
| A "recently closed" section in the inbox, or a closed card in open views | Closed card only | R14 |
| May a guard run only at some stages | Yes, per guard, all stages by default | R12 |
| Which kinds | `AlreadyDone`, `Changed`, `NotApplicable` | R13 |
