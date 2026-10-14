# Single Proposal Reads Plan

This document is the architecture and implementation plan for reading agent proposals one at a
time: by id, and as a bounded batch of ids. It extends the
[autonomous agent runs plan](ai-autonomous-agent-runs-plan.md) and builds on the
[proposal revalidation plan](ai-proposal-revalidation-plan.md) and the
[rule runs plan](ai-agent-rule-runs-plan.md). It is a plan, not usage documentation: once the
feature exists, the consumer guide `ai-agent-runs` and the sample cases are authoritative.

The plan answers consumer feedback. A consumer shows agent proposals in its own screens and
refreshes them on the live `AgentWorkChanged` message, which names the proposal that changed. To
read that one proposal it has to load the whole thread. This plan treats the request as a
description of what is missing, verifies its claims against the code and keeps what holds up;
where the code works differently or a simpler design fits better, it says so in
[Assessment](#assessment-of-the-feedback). It was checked against the code on 2026-10-14.

## Status

Implemented on 2026-10-15, with sample case SPM-282 extended. The choices are part of the
[decision record](#decision-record). The consumer guide `ai-agent-runs` and the sample are
authoritative from now on; the implementation deviates from the plan where
[Implementation notes](#implementation-notes) says so.

## Implementation notes

- **Reads** live in `NhAssistantProposalReads`, which both endpoints use. It loads the proposals
  with their threads in one query, drops threads of another tenant and hidden shadow agents, and
  resolves each distinct subject once; the read-only `NhAssistantThreadService.ReadAccessAsync`
  then grants access by role or by an invitation to the thread's conversation, as the thread
  endpoints do, without adding or removing participants.
- **The tenant check** compares the thread's tenant with the caller's, as reading the thread's
  conversation does; the thread endpoints leave it to the subject's resolver.
- **The decision right** uses `NhAssistantApproverCheck` with the subject described once per
  subject (`NhAssistantResolvedSubject`). A pending proposal past its expiry, or one that a person's
  own turn decides, has no decision right.
- **Duplicates** in a lookup count once before the limit is checked, so 101 copies of one id are
  one id.
- **The sample's task guard** now names who completed the task with `ByActorId`, so SPM-282 shows
  `staleByActorId` next to the explanation.

## Problem

- `AgentWorkChanged` names one proposal (`ApprovalId`), but no endpoint returns one proposal.
  `approvals/{approvalId}` has `decisions`, `feedback` and `resolve`, and no `GET`.
- The proposal lists each answer a different question: `threads/{id}/proposals` returns the 200
  newest proposals of one thread in every status, `approvals/inbox` returns open proposals, and
  `subjects/{type}/{id}/work` returns the open proposals of one subject. A proposal that just
  closed, executed or was replaced can only be read through its whole thread.
- The library pays that price itself. When a proposal leaves the inbox or the subject suggestions
  during a live reload, `NhAssistantAgentProposalsComponent` reads every thread such a proposal
  came from, up to 200 proposals each, to find out whether it closed as stale or was replaced.

## Assessment of the feedback

### Verified claims

| Claim | Verified | Where |
|---|---|---|
| `AgentWorkChanged` names one approval | Yes; `NhAssistantLiveAgentWorkDto.ApprovalId` for reason `proposal` | `AI.Chat.AspNet/NhAssistantContracts.cs` |
| The only way to read it is the thread's proposals | Yes for a proposal in any status; the inbox and the subject work return open proposals only | `AI.Chat.AspNet/AgentRuns/NhAssistantAgentRunEndpoints.cs` |
| The inbox returns only pending proposals the caller may decide | Partly. It returns pending, unexpired proposals whose subject lists the caller as an approver in the member snapshot, or that the caller started. It does not check whether the caller may decide them. | `NhAssistantRunStore.ListInboxAsync` |
| `canDecide` per caller exists in the inbox | No. The live check is `NhAssistantApproverCheck` (approvers, four-eyes, the tool's approve policy), used by the subject work and by `thread.decisions` of a conversation. | `AgentRuns/NhAssistantProposalDecisions.cs`, `AgentRuns/NhAssistantSubjectWork.cs` |
| The client has a store for agent work | No. The inbox, the thread view and the subject suggestions each reload on `AgentWorkChanged` through `nhAssistantReloadOnAgentWork`. | `nh-ai-chat/agent-runs/src` |

### What the feedback gets right

- A proposal is a resource with an id that live updates name; it should be readable by that id,
  in every status, with the same access rules as its thread and without revealing that it exists
  to people who may not see it.
- A batch read serves screens that refresh several proposals at once, and it replaces the thread
  reads the library does today for closed cards.
- The answer should say whether the caller may decide the proposal, so a client does not derive
  it from the role.

### What this plan does differently, and why

- **At most 100 ids per lookup, not 200.** Every distinct subject in a lookup costs a live call to
  the application's `INhAiSubjectAccessResolver`; the member snapshot that the subject summary
  counts from may not decide who reads a proposal's content. Every pending proposal also costs an
  approver check. 100 is the largest page any proposal list returns (`approvals/inbox`).
- **The decision right comes from the live approver check**, as in the subject work, not from the
  inbox. The single read and the lookup return the subject work's shape: the proposal with
  `canDecide` and `reasonCode`.
- **No store method; API methods and the library's own use.** The library has no agent-work store
  to extend. `NhAssistantAgentRunApiService` gets `getProposal` and `lookupProposals`, which is
  what an application with its own screens needs. The library uses the lookup where it saves the
  most: closed cards no longer read threads.
- **No in-place refresh of one card per event.** A proposal that a list does not show yet, such as
  a new one, can only come from reloading the list; the inbox reload is one request of at most 20
  proposals. Patching single cards would duplicate each list's filter, order and total in the
  client. The guarantee becomes "no thread reads on a live update" instead of "one card per
  event".

### What the feedback missed

- **Reading must not join the thread.** `NhAssistantThreadService.ResolveAccessAsync`, which the
  thread endpoints use, adds the caller to the thread's conversation as a participant and removes
  a participant who lost their role. A lookup over many threads must check access without
  changing who takes part in which conversation.
- **Shadow work.** Proposals of a shadow agent are visible to administrators only, like its
  threads.
- **A person's own chat proposals** have no agent thread; they stay readable through their
  conversation and are not found here.
- **The approver check resolves the subject again for every proposal.** A lookup resolves each
  distinct subject once and passes the result to the check.

## Goals

- `GET approvals/{approvalId}` returns one agent proposal in any status with the caller's
  decision right, or 404 when it does not exist or the caller may not see it.
- `POST approvals/lookup` returns the visible proposals among at most 100 ids and leaves the rest
  out.
- The library's closed cards read proposals by id instead of whole threads.

## Non-goals

- Content in live updates; `AgentWorkChanged` stays content-free.
- Reading a person's own chat proposals outside agent threads.
- A client store for agent work, or patching single cards in the library's lists.
- Changing the inbox's selection or adding the decision right to the inbox.

## Baseline

Paths are relative to `src/Back-end/Libraries` and `src/Front-end/projects/nh-ai-chat`.

| Area | Exists today | Change |
|---|---|---|
| Endpoints | `threads/{id}/proposals`, `approvals/inbox`, `subjects/{type}/{id}/work`; `approvals/{id}/decisions`, `feedback`, `resolve` | `GET approvals/{approvalId}`, `POST approvals/lookup` |
| Thread access | `NhAssistantThreadService.ResolveAccessAsync` resolves the live role and joins or removes the caller | A read-only access check without side effects |
| Decision right | `NhAssistantApproverCheck.CheckAsync` describes and resolves the subject per proposal | An overload that takes the resolved subject |
| DTOs | `NhAssistantSubjectProposalDto(Proposal, CanDecide, ReasonCode)` for open proposals of a subject | Also the answer of both reads; `NhAssistantProposalLookupRequest` and `NhAssistantProposalLookupDto` |
| Client API | `getProposals(conversationId)`, `getInbox`, `getSubjectWork` | `getProposal(approvalId)`, `lookupProposals(approvalIds)` |
| Closed cards | `holdStale` reads `getProposals` for every thread of a vanished proposal | One `lookupProposals` call |

## Design

### Reading one proposal

`GET {prefix}/approvals/{approvalId}` returns `NhAssistantSubjectProposalDto`:

- `proposal`: the `NhAssistantProposalDto` as the thread list returns it, in any status, completed
  by `NhAssistantAgentRunDtoMapper.CompleteAsync` with `objects`, `staleKind`,
  `staleExplanation`, `staleByActorId`, `staleByDisplayName`, `supersededByApprovalId` and
  `rationale`, and with the caller's own `feedback` and `canGiveFeedback`;
- `canDecide` and `reasonCode`: the live approver check for a pending proposal, as the subject
  work computes it (`assistant-four-eyes-required`, `assistant-approval-policy-denied`, and
  `assistant-approval-decision-invalid` for a proposal that a person's own turn decides). For a
  proposal in any other status `canDecide` is `false` and `reasonCode` is `null`; the status says
  why.

The caller needs the reader role or more on the thread's subject, checked live through the
subject's resolver, and an administrator role for a shadow agent's proposal. Everything else
answers `404 assistant-approval-not-found`, the same answer as for an id that does not exist: a
proposal outside an agent thread, a subject in another tenant or division, a subject the caller
has no role on and a hidden shadow proposal.

### Reading many proposals

`POST {prefix}/approvals/lookup` with `{ "approvalIds": [...] }` returns
`{ "items": NhAssistantSubjectProposalDto[] }`:

- 1 to 100 ids; an empty list, more than 100 distinct ids or a malformed body answers
  `400 assistant-message-invalid`. Duplicates count once.
- The items follow the order of the request. Ids the caller may not see, or that do not exist,
  are left out without a reason, so the answer reveals nothing about them.
- The store loads the proposals with their threads in one query. Each distinct thread's subject is
  resolved once; the approver check of every pending proposal reuses that result. Shadow threads
  are dropped for non-administrators before anything is resolved.

### Access without side effects

`NhAssistantThreadService` gets a read-only check that returns the thread and the caller's live
role without adding or removing conversation participants. Both reads use it. The thread
endpoints keep `ResolveAccessAsync`, because opening a thread is what makes someone take part in
it.

### Client (`@newheap/platform-ai-chat`)

- `NhAssistantAgentRunApiService.getProposal(approvalId)` returns `AgentSubjectWorkProposal`;
  `lookupProposals(approvalIds)` returns `AgentProposalLookup` with `items`.
- `NhAssistantAgentProposalsComponent` keeps closed cards through one `lookupProposals` call for
  the proposals that left the list, instead of reading each of their threads. The inbox and the
  subject suggestions therefore never read a thread on a live update.
- Lists keep reloading on `AgentWorkChanged` as they do; the reload stays the way new proposals
  appear.

### Documentation

The guidance rule `nh-ai-agent-runs` (and so the consumer guide `ai-agent-runs`) describes the
reads next to the live updates: refresh a known proposal with `GET approvals/{id}` or a batch with
`approvals/lookup`, reload a list for new ones. The release notes list both endpoints, the client
methods and the closed cards without thread reads.

## Decision record

Proposed on 2026-10-14.

| # | Decision | Rationale |
|---|---|---|
| D1 | `GET approvals/{approvalId}` for agent-thread proposals in any status. | The live update names the proposal; it needs a resource of its own. |
| D2 | `POST approvals/lookup` with at most 100 ids, request order, invisible ids left out. | Batch refresh without leaking existence; every subject costs a live resolver call. |
| D3 | Both return `NhAssistantSubjectProposalDto`: the proposal with the live `canDecide` and `reasonCode` for pending proposals. | One shape for "a proposal and my right to decide it"; the inbox has no such check. |
| D4 | Access as the thread's proposals (live reader role, shadow for administrators), checked without joining the conversation; anything invisible answers 404. | Same visibility as the thread, no side effects from reading. |
| D5 | Chat proposals of a person's own turn are not readable here. | They have no agent thread; their conversation shows them. |
| D6 | The client gets API methods, and closed cards use the lookup; no store and no single-card patching. | The library has no store; patching duplicates list semantics for little gain. |
| D7 | The approver check accepts a resolved subject, so a lookup resolves each subject once. | Bounded cost per request. |
| D8 | Extend sample case SPM-282 instead of adding a case. | The closed cards of SPM-282 are the library's own consumer of the lookup. |
| D9 | Additive; next minor release of nuget-common and npm-ai-chat, no migration. | New endpoints, DTOs and client methods only. |

## Delivery phases

1. **Server.** The read-only thread access, the approver check overload, the store query by ids,
   both endpoints, DTOs and serializer registrations.
2. **Client.** `getProposal`, `lookupProposals` and their models; closed cards through the lookup.
3. **Evidence and documentation.** SPM-282 extended, the guidance rule and consumer guide
   `ai-agent-runs`, release notes and this plan's implementation status.

## Verification

On SQL Server and PostgreSQL:

- `GET approvals/{id}` returns a proposal in each status (`pending`, `approved`, `executing`,
  `executed`, `execution-failed`, `execution-unknown`, `rejected`, `expired`, `withdrawn`,
  `stale`, `resolved`, and `shadow` for an administrator) with its objects, stale kind and
  explanation, successor and rationale.
- A reader gets `canDecide: false`; an approver gets `true`; the initiator under four-eyes gets
  `false` with `assistant-four-eyes-required`; a decided proposal has no decision right.
- The caller's own feedback and `canGiveFeedback` match the thread's list.
- A caller without a role, in another division, for a hidden shadow proposal and for a chat
  proposal gets `404 assistant-approval-not-found`, as for an unknown id.
- Reading does not add the caller to the thread's conversation.
- The lookup returns only visible proposals in request order, counts duplicates once, refuses an
  empty list and more than 100 ids, and resolves each subject once.

In the client:

- `getProposal` and `lookupProposals` call the endpoints with the right body.
- The inbox and the subject suggestions keep a proposal that closed as stale or was replaced as a
  closed card through one lookup and never call `getProposals` on a live update.

Sample case SPM-282 extended: after the approver ticks the task off by hand, the closed proposal
reads as already done through `GET approvals/{id}` and through the lookup together with the open
status proposal; someone outside the project gets 404 and an empty lookup.

## Risks

- **Resolver cost.** A lookup with 100 proposals on 100 subjects makes 100 resolver calls and up
  to 100 approver checks. The limit bounds it; applications with expensive resolvers should look
  up what they show, not everything they know.
- **Stale decision rights.** `canDecide` is a snapshot; the decision endpoints check again, as
  they do for the subject work.
- **Clients polling by id.** Reading by id is cheap but not free. The guide keeps live updates as
  the trigger to read.

## Answered questions

- **200 or 100 ids?** 100: the live resolver runs per subject, and no proposal list pages beyond
  100.
- **Where does `canDecide` come from?** The live approver check of the subject work, not the
  inbox, which has none.
- **A store method?** No store exists; API methods serve applications, and the library uses the
  lookup for closed cards.
- **Should reading join the thread?** No; only opening a thread does.
- **A new sample case?** No; SPM-282 already shows the closed cards that use the lookup.
