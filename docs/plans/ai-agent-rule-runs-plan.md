# Rule Runs and Proposal Deduplication Plan

This document is the architecture and implementation plan for two additions to agent runs:
agents that make proposals with deterministic application rules instead of a language model,
and proposals that do not pile up when the same work is proposed again. It extends the
[autonomous agent runs plan](ai-autonomous-agent-runs-plan.md) and builds on the
[proposal revalidation plan](ai-proposal-revalidation-plan.md). It is a plan, not usage
documentation: once the features exist, the consumer guide `ai-agent-runs` and the sample cases
are authoritative.

The plan answers consumer feedback. A consumer lets agents propose deterministic work, such as
retrying a failed synchronization, recording production above the agreed quantity as additional
work and completing office tasks that are evidently done, and wants those proposals in the same
governed flow as model proposals without paying for model calls. The same consumer sees a daily
round propose the same thing every day. This plan treats the request as a description of what is
missing, verifies its claims against the code and keeps what holds up; where the code works
differently or a simpler design fits better, it says so in [Assessment](#assessment-of-the-feedback).
It was checked against the code on 2026-10-14.

## Status

Implemented on 2026-10-14, with sample case SPM-283. The choices are part of the
[decision record](#decision-record). The consumer guide `ai-agent-runs` and the sample are
authoritative from now on; the implementation deviates from the plan where
[Implementation notes](#implementation-notes) says so.

## Implementation notes

- **Rule turns** run inside the agent's run, in the same turn bookkeeping as a model turn.
  `ProposeAsync` goes through `NhAssistantToolCallInterceptor` with `RequireApprovalForEffects` on
  the agent scope, so the tool invoker never executes the tool: tools without approval, read-only
  tools, issuer tools and tools outside the agent's selectors are `Refused`. Rule proposals are
  always deferred.
- **Which runs use the rules.** Every headless run of an agent with a rule handler, including
  manual starts, agent requests and follow-ups, unless its trigger sets `UseModel()`. A person's
  message in the thread and a consultation always reach the model.
- **Rationales** are rendered per supported culture into `RationalesJson`. The rule's message in
  the thread is the handler's summary followed by the rationales of the proposals it created, one
  "- " line each, in the run's culture; the model reads them when a person asks about a proposal.
- **The rule's message** stores `rule:{agentId}` as its author. Message DTOs never expose the author
  of an agent message and report `AuthorKind = "rule"` for it.
- **Identity** is the SHA-256 of the tool id and the canonical arguments JSON. Proposals recorded
  before the migration have no hash and are compared only by their objects.
- **Duplicates** are recorded as tool calls that read `not-executed` with the duplicate's code, so
  the thread shows the attempt, but they do not count as proposals of the run and do not notify.
- **Superseding** withdraws the older proposal through the same transition as a decision, with
  `assistant-proposal-superseded` as its result code; its tool call reads `not-executed` as
  withdrawn, and a run that waits for it inline continues with a "superseded" result. The approval
  part of a conversation also carries `SupersededByApprovalId`, so the chat's approval card reads
  "Replaced by a newer proposal" too.
- **Objects and names** are added to proposal lists, the inbox, decision results and the subject's
  work by `NhAssistantAgentRunDtoMapper.CompleteAsync`, with one query for the keys and one for the
  names of the whole list.
- **Payloads** larger than `MaxInputPayloadCharacters` throw when the trigger is published, as a
  programming error, instead of being cut off.
- **The client** moves focus to the successor of a replaced proposal when it is in the same list;
  the inbox and the subject suggestions keep replaced proposals as closed cards, as they do for
  stale ones.
- **The sample's** status guard names no objects, so SPM-283 demonstrates identity and the
  rejection cooldown; replacing a proposal for the same objects is covered by the library tests.
- **Known limitation.** Governed argument binding ignores unknown properties, so `ProposeAsync`
  refuses arguments of the wrong type but not arguments with extra properties.

## Problem

- A proposal exists only when a language model calls a tool that requires approval. An
  application whose agent decides by fixed rules has to fake a model: a chat client that runs C#
  rules when a trigger message carries a marker line and forwards to the real model otherwise.
  That costs nothing, but it misuses message text, depends on the chat profile setup and cannot
  ask the platform which proposals are open, so it reads the assistant's tables directly.
- Nothing stops a proposal from being made twice. Two runs that propose the same change for the
  same object leave two open proposals, and a scheduled round proposes the same thing every day.
- A proposal that people rejected comes back the next day. The model reads recent rejections in
  its continuity block; a rule, or a model that ignores the block, does not.
- Clients cannot group proposals by the application object they change: `targets` holds the
  invocation's execution scopes, and the objects that proposal guards name are not exposed.

## Assessment of the feedback

### Verified claims

| Claim | Verified | Where |
|---|---|---|
| Proposals are created only inside a model turn | Yes; `NhAssistantToolCallInterceptor.RequestApprovalAsync` is the only caller of `AddApprovalAsync`. `INhAiProposalFactory` only builds the proposal object. | `AI.Chat/Runtime/NhAssistantToolCallInterceptor.cs` |
| There is no deduplication of open proposals | Yes; the idempotency manager guards executions, not proposals | `AI.Chat/Governance/NhAssistantIdempotencyManager.cs` |
| The stale explanation is needed for "already done by Henk on 14 Oct" | Already delivered: `StaleKind` and `StaleExplanation` on `NhAssistantProposalDto` and on approval parts. Only a structured "by whom" is missing. | Proposal revalidation |
| Affected-object keys exist for revalidation | Yes; `AssistantApprovalKey`, indexed per scope key, not exposed in DTOs | `AI.Chat/Persistence` |

### What the feedback gets right

- Deterministic proposals belong in the platform, in the same governed flow as model proposals:
  thread, approval policy, presentation, guards and revalidation, inbox, live updates, audit,
  budget, shadow mode and the kill switch.
- Hybrid agents: rules for headless runs, the agent's model when a person types in the thread.
- Identical proposals must not pile up, for model and rule proposals alike.
- Clients need the affected objects to group proposals.

### What this plan does differently, and why

- **No `ReadAsync` through tools.** A rule handler is application code with its own service
  scope; it reads the application's data directly. Reading through governed tools exists for the
  model, which can act only through tools. Leaving it out keeps the contract small; it can be
  added when a rule needs another system's tool.
- **`ProposeAsync` accepts only tools that require approval.** It runs through the same
  interceptor as a model call. A tool that executes without approval would otherwise run
  silently from a method called "propose".
- **Rule proposals are always deferred.** An inline proposal suspends the run so the model can
  continue with the decision. A rule run has no model to continue, so its proposals wait in the
  inbox like deferred ones, whatever the policy's mode.
- **Rules first, the model when a case is unclear.** Instead of a per-trigger switch only, the
  handler decides per run: it proposes and finishes, or hands the run over to the model with a
  reason. Clear cases cost nothing; unclear ones get the model. A trigger can still opt out of
  rules with `UseModel()`.
- **Two levels of duplicates, not one.** The same tool with the same canonical arguments is the
  same proposal: it is not recorded again. The same tool for the same objects with other
  arguments is a newer version of the proposal: by default it replaces the open one, which is
  withdrawn as superseded. Skipping it, as the feedback suggests, would keep the first day's
  value while production above the agreed quantity grows every day. Policies can choose skip or
  allow instead.
- **The rule works from the subject's current state.** A run keeps the trigger's message text,
  not the typed event; events coalesce into one run, and a retry or a schedule has no event at
  all. The handler therefore evaluates the subject as it is now, which is also what makes a rule
  idempotent. A trigger can store a small typed payload per event (`Payload`) for rules that
  need the event's facts.

### What the feedback missed

- **Proposals that come back after a rejection.** Deduplicating open proposals does not stop a
  proposal rejected on Monday from returning on Tuesday. A rejection cooldown on the approval
  policy refuses the same proposal, or one for the same objects, for a period after a rejection;
  the caller learns when it was rejected.
- **A rule has to say why.** In a hybrid thread a person asks "why do you propose this?"; the
  model can only answer from what the thread holds. `ProposeAsync` takes a rationale, rendered in
  every supported culture, which approvers read with the proposal and the model reads in the
  thread's history.
- **Who made a proposal stale.** The revalidation explanation names the person in text; a
  structured actor lets clients show it consistently.
- **Consultations stay with the model.** Another assistant consults an agent with a question;
  rules do not answer questions.
- **Duplicates must not inflate counts.** A run that finds its proposal already open must not
  count it, notify about it or audit it as a new proposal.

## Goals

- Applications register a rule handler per agent that makes proposals through the governed flow
  without a model call, and can hand a run over to the model.
- A person's turn in the thread of such an agent always uses the agent's model profile.
- An identical open proposal is never recorded twice; a newer proposal for the same objects
  replaces the open one by default; a recently rejected proposal is not made again.
- Proposals expose the objects they change and who made them stale.

## Non-goals

- Reading through governed tools from rule handlers.
- Executing without approval from a rule handler; rules propose, people decide.
- Deduplicating a person's own chat proposals; the person decides them inline.
- Deduplicating across agents; each agent answers for its own proposals.
- Grouping proposals by object in the library's inbox; clients group with `objects`.

## Baseline

Paths are relative to `src/Back-end/Libraries`.

| Area | Exists today | Change |
|---|---|---|
| Run engine | `NhAssistantAgentRunOperation` calls `INhAssistantTurnRunner.RunAgentTurnAsync`, which always runs the model | Branches to a rule turn when the agent has a rule handler |
| Tool calls | `NhAssistantToolCallInterceptor.InvokeAsync` governs every model tool call and records proposals | The rule context invokes it for `ProposeAsync` |
| Run inputs | `AssistantQueuedInput` holds trigger id and text; the typed event is gone at run time | Optional `PayloadJson` per input |
| Approval policy | `NhAiApprovalPolicyBuilder`: mode, lifetime, approvers, four-eyes, reminders, expiry, unchanged subject, binding, follow-up | `Duplicates(...)` and `RejectionCooldown(...)` |
| Proposals | `AssistantApproval` with keys in `AssistantApprovalKey` | `ArgumentsHash`, `SupersededByApprovalId`, `RationalesJson`, `StaleByActorId` |
| Runs | `AssistantRun` without a kind; `NhAssistantRunDto` | `Kind`: `model`, `rule`, `rule-then-model` |
| Messages | Author kinds `person` and `trigger` | `rule` for the summary message of a rule run |

## Design

### Rule handlers

```csharp
// NewHeap.Platform.AI.Chat
public interface INhAssistantAgentRuleHandler
{
    Task<NhAssistantRuleResult> RunAsync(NhAssistantRuleContext context, CancellationToken cancellationToken);
}

assistant.ConfigureAgent("sync-guard", agent => agent
    .UseRuleHandler<SyncRetryRules>()
    .UseApprovalPolicy("sync-review"));

assistant.AddTrigger<SyncFailed>("sync-failed", trigger => trigger
    .StartAgent("sync-guard")
    .Subject(evt => new NhAiSubjectRef("sync", evt.SyncId.ToString()))
    .Message(evt => $"Synchronization {evt.SyncId} failed.")
    .Payload(evt => new { evt.SyncId, evt.ErrorCode }));     // optional, small

assistant.AddTrigger<OrderQuestion>("order-question", trigger => trigger
    .StartAgent("sync-guard")
    .UseModel()                                               // this trigger skips the rules
    ...);
```

```csharp
public sealed class SyncRetryRules(AppDbContext db) : INhAssistantAgentRuleHandler
{
    public async Task<NhAssistantRuleResult> RunAsync(NhAssistantRuleContext context, CancellationToken ct)
    {
        var sync = await db.Syncs.FindAsync([Guid.Parse(context.Subject.Subject.Id)], ct);
        if (sync is null || sync.Status != SyncStatus.Failed)
        {
            return NhAssistantRuleResult.Done("Nothing to retry.");
        }
        if (sync.Failures > 5)
        {
            return NhAssistantRuleResult.HandOver("Failed more than five times; needs judgement.");
        }

        var proposed = await context.ProposeAsync(
            "syncs.retry",
            new SyncRetryInput(sync.Id),
            culture => Texts.RetryRationale(culture, sync.Failures, sync.LastError),
            ct);
        return NhAssistantRuleResult.Done(proposed.Outcome switch
        {
            NhAssistantProposeOutcome.Created => "Proposed a retry.",
            NhAssistantProposeOutcome.AlreadyOpen => "A retry already waits for a decision.",
            NhAssistantProposeOutcome.RecentlyRejected => "A retry was rejected recently.",
            _ => "No retry proposed."
        });
    }
}
```

- `UseRuleHandler<THandler>()` registers the handler scoped and applies it to every headless run
  of the agent: event, schedule, webhook, manual start, agent request and follow-up runs. A
  trigger opts out with `UseModel()`. A person's turn in the thread and consultations always use
  the agent's model profile, which stays required.
- `NhAssistantRuleContext` gives the agent id, run id, trigger id, subject (`NhAiSubjectInfo`),
  initiator, accountable owner, shadow flag, the run's inputs (trigger id, text and optional
  payload with `GetPayload<T>()`), the working memory with `UpdateMemoryAsync(patch)` under the
  same schema rules as `memory_update`, the last run summaries, and the thread's recent rejections
  and invalidations.
- `ProposeAsync(toolId, arguments, rationale)` looks the tool up among the agent's offered tools
  (its selectors, grants and delegation, exactly as for the model) and invokes it through the
  interceptor. The tool call, the proposal, its presentation in every supported culture, its keys,
  guards, deduplication, audit and live update are the same as for a model call. It returns
  `NhAssistantProposeResult` with an outcome (`Created`, `AlreadyOpen`, `RecentlyRejected` or
  `Refused` with a code), the approval id and the superseded approval id. It refuses tools outside
  the agent's selectors, tools that do not require approval and invalid arguments, and counts
  against the turn's tool-call limit and the agent's budget.
- Rule proposals are deferred, whatever the policy's mode; shadow runs record shadow proposals.
- `NhAssistantRuleResult.Done(summary)` ends the run: the summary becomes the run summary and a
  message of author kind `rule` in the thread, listing the proposals with their rationale in the
  default culture. `HandOver(reason)` continues the same run as a model turn with the reason in
  the turn's context; the run's kind becomes `rule-then-model`.
- A rule turn calls no model and records zero tokens. Everything else of a run stays: background
  operation, retry and cancel, kill switch, budgets and run limits, notifications,
  `AgentWorkChanged`, audit, summaries and follow-up runs. Business audit marks the run's kind.

### Duplicates and rejections

```csharp
assistant.AddApprovalPolicy("sync-review", policy => policy
    .Mode(NhAiApprovalMode.Deferred)
    .Approvers(NhAiApprovers.SubjectRole(NhAiSubjectRole.Approver))
    .Duplicates(NhAiDuplicateProposals.Supersede)      // default; Skip or Allow
    .RejectionCooldown(TimeSpan.FromDays(7)));         // default; TimeSpan.Zero turns it off
```

- **Identity.** A proposal's identity is its tool id and a hash of its canonical arguments
  (`ArgumentsHash`, indexed). Its objects are the keys its guards name.
- **Before a proposal is recorded**, within the same agent and scope key:
  1. An open proposal (pending, approved or executing) with the same identity: nothing is
     recorded; the caller gets the open proposal as `AlreadyOpen`.
  2. A pending proposal of the same tool for the same objects, with other arguments:
     `Supersede` records the new proposal and withdraws the old one as `superseded`, linked to the
     new one, in one transaction; `Skip` records nothing and returns the open one as
     `AlreadyOpen`; `Allow` records both. An approved or executing one is never superseded; the
     new proposal is `AlreadyOpen`.
  3. A proposal with the same identity, or for the same objects when the tool has keys, that was
     rejected within the cooldown: nothing is recorded; the caller gets `RecentlyRejected` with the
     date of the rejection.
- The model receives a synthetic result for 1 and 3, like the "recorded" result: the proposal
  already waits, or was rejected on a date, and must not be proposed again.
- Deduplication applies to proposals of agent runs and consultations, for model and rule runs
  alike. It is one indexed query on the identity and, for tools with keys, one on the key index
  of revalidation. Duplicates do not count as proposals of the run, do not notify and are audited
  as `AgentProposalDeduplicated` without content.
- A superseded proposal is `withdrawn` with result code `assistant-proposal-superseded`; its tool
  call reads `not-executed`, the live update reaches the subject's members, and an inline run that
  waits for it is woken as for a stale proposal.

### Objects and who made a proposal stale

- `NhAssistantProposalDto.Objects` lists the objects a proposal changes as `{ type, id }`, also in
  the inbox, the subject work and the thread's decisions. A guard that only names keys and always
  returns `Satisfied` is enough to expose objects for a tool.
- `NhAiPreconditionResult.ByActorId` lets a guard name who made the proposal stale.
  `NhAssistantProposalDto` exposes `StaleByActorId` and `StaleByDisplayName` from the member
  snapshot.
- `NhAssistantProposalDto.SupersededByApprovalId` links a superseded proposal to its successor.

### Client (`@newheap/platform-ai-chat`)

- The run timeline and thread view show a rule run as "Automatic rule" ("Automatische regel"),
  and a handed-over run as "Rule, then the model".
- A message of author kind `rule` reads as the agent's rule, not as the model.
- A superseded proposal reads "Replaced by a newer proposal" and links to it.
- `AgentProposal.objects`, `staleByActorId` and `staleByDisplayName` are added to the models.

## Decision record

Proposed on 2026-10-14.

| # | Decision | Rationale |
|---|---|---|
| D1 | `INhAssistantAgentRuleHandler` per agent with `UseRuleHandler<T>()`, applied to every headless run; `UseModel()` per trigger opts out. | One registration makes an agent rule-based; exceptions are explicit. |
| D2 | The handler returns `Done(summary)` or `HandOver(reason)`; a hand-over continues the run as a model turn. | Clear cases cost nothing, unclear ones get judgement, in one run. |
| D3 | A person's turn and consultations always use the model profile, which stays required. | People ask questions; rules do not answer them. |
| D4 | `ProposeAsync` runs through `NhAssistantToolCallInterceptor` with the agent's offered tools and accepts only tools that require approval. | One governed path; "propose" never executes. |
| D5 | No `ReadAsync`; handlers read application data directly. | Rules are application code; tool reads exist for the model. |
| D6 | Rule proposals are deferred whatever the policy's mode. | A rule run has no model to continue after an inline decision. |
| D7 | Handlers evaluate the subject's current state; triggers may store a small typed `Payload` per input. | Runs keep no typed event; coalescing, retries and schedules have none. |
| D8 | Duplicates on two levels: identity (tool and canonical arguments) is never recorded twice; same objects follow `Duplicates(Supersede | Skip | Allow)`, default `Supersede`. | Identical work must not pile up; changed values must replace stale ones. |
| D9 | `RejectionCooldown`, default seven days, by identity or by objects for tools with keys. | Rejected proposals must not return the next day, from rules or from a model. |
| D10 | Deduplication covers agent-run and consultation proposals of the same agent and scope key, not a person's chat proposals and not other agents. | The proposing agent answers for its proposals; a person decides their own inline. |
| D11 | `ProposeAsync` takes a rationale rendered per culture; it reaches approvers and the thread's history. | Approvers and the model in a hybrid thread need the rule's reason. |
| D12 | `Objects` on the proposal DTO as `{ type, id }`; `ByActorId` on guard results; `SupersededByApprovalId`. | Grouping and attribution without parsing strings. |
| D13 | Run kind `model`, `rule` or `rule-then-model`; message author kind `rule`; zero tokens for rule turns. | Timelines, budgets and audits tell rule work from model work. |
| D14 | Next minor release of nuget-common and npm-ai-chat; one `nhai` migration. | Additive, with new defaults documented. |

## Delivery phases

1. **Duplicates and rejections.** `ArgumentsHash`, the policy options, the checks before recording,
   superseding with its live update, audit and wake-up, the synthetic model results, and the
   counts that ignore duplicates. Model runs benefit at once.
2. **Objects and attribution.** `Objects`, `ByActorId` and `SupersededByApprovalId` in the DTOs.
3. **Rule runs.** The handler contract and registration, the rule turn in the turn runner,
   `ProposeAsync` through the interceptor, rationales, run kinds, the rule message, payloads on
   inputs and the hand-over to the model.
4. **Client.** Run kinds, the rule message, superseded proposals and the new model fields.
5. **Evidence and documentation.** Sample case SPM-283, the guidance rule and consumer guide
   `ai-agent-runs`, release notes and the agent-runs plan's implementation status.

## Verification

On SQL Server and PostgreSQL:

- A rule handler started from a domain event records a deferred proposal while the agent's chat
  client is never called, writes its summary as a `rule` message, records zero tokens, and the
  proposal executes after approval.
- A person's turn in the same thread reaches the model, which sees the rule's rationale.
- A handler that hands over continues the run with the model; the run reads `rule-then-model`.
- Shadow mode, the kill switch, the daily budget and the run limits apply to rule runs.
- Running the same rule twice leaves one open proposal and the second run gets `AlreadyOpen`; a
  model calling the tool twice gets the "already proposed" result and one proposal remains.
- A proposal for the same objects with other arguments supersedes the open one, which reads
  `withdrawn` with `superseded` and links to its successor; `Skip` and `Allow` behave as
  configured; an approved proposal is never superseded.
- A rejected proposal is refused as `RecentlyRejected` within the cooldown and accepted after it.
- Duplicates do not count, notify or audit as new proposals.
- `ProposeAsync` refuses a tool outside the agent's selectors and a tool without approval.
- The proposal DTO carries the objects, the stale actor and the successor.

Sample case SPM-283: a project closer agent proposes, by rule, to complete a project once all its
tasks are completed. A second task update does not add a second proposal; a rejection keeps the
rule quiet for the cooldown; a project member asks the agent in the thread why it proposed it, and
the scripted model answers from the rule's rationale.

## Risks

- **Silent suppression.** A wrong identity or object key hides a legitimate proposal. Mitigation:
  duplicates are audited and counted per tool, `Allow` turns it off per policy, and the caller
  always learns the outcome.
- **Rules that drift from the model's behavior.** Hybrid agents may propose differently by rule
  and by model. Mitigation: the rationale is visible, and the hand-over keeps judgement for
  unclear cases.
- **Changed defaults.** Existing agents get deduplication and a cooldown. Mitigation: release
  notes with adoption notes; `Allow` and `TimeSpan.Zero` restore the old behavior.
- **Payload growth.** Typed payloads per input are bounded and optional.

## Answered questions

The assessment left five choices. The advice was accepted on 2026-10-14:

| Question | Answer | Decision |
|---|---|---|
| A newer proposal for the same objects | Supersede by default; skip or allow per policy | D8 |
| A cooldown after rejection | Yes, on the policy, seven days by default | D9 |
| Scope of deduplication | Same agent and scope key | D10 |
| `ReadAsync` through tools | Not now | D5 |
| Rule agents without a model profile | The profile stays required | D3 |
