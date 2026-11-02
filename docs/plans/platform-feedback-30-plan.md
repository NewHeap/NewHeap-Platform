# Platform Feedback on 30.0.0 Plan

A consuming application with hundreds of open agent proposals reported six points on NuGet 30.0.0 and
`@newheap/platform-ai-chat` 0.27.0, numbered FB78 to FB83 in its own plan. With three proposals the inbox and the
board work; with hundreds they are an endless column without counts, kinds or keyboard, and a person ends up
approving page by page. Each finding was checked against `main` at f7402f69. FB83 was already fixed there; the
other five hold, and FB78 needs one correction. The check also found a bug of its own (FB84). This plan records one
choice per finding. It is a plan, not usage documentation: once it is built, the guidance rules, the release notes
and the sample cases are authoritative.

## Status

Decided by Lars on 2026-11-02, one by one: FB78, FB79, FB80, FB84, FB81 and FB85 as advised (option (a)), and FB82
option (c), with the follow-up choice that an administrator switches a kind of action on as data, first in shadow,
once its tool allows it in code. FB83 was fixed before the plan. The decisions are summarized below; the sections
under "Choices" keep the options that were weighed. Built on 2026-11-02 in train 33, with lanes DS (the numbers of FB82),
QB (FB78, FB80), AE (running a kind without approval, FB82) and QU (FB79, FB81, FB84), and verified together. The
verification found one cross-lane contract test (the administration's tool list now names `allowWithoutApproval`),
fixed in the train. The choices each lane made are in the "Lane choices" table below; Lars kept all 41 on 2026-11-02.

## Decisions

| # | Decision |
|---|---|
| FB78 | **`GET approvals/inbox/summary`** counts per agent, kind of action, object type and "expires within 24 hours" with the inbox's filter and visibility; the inbox takes `toolId`, `expiresBefore` and a sort, and `NhAssistantAgentInbox` the same inputs. |
| FB79 | **`density: 'compact'`** for the proposals list and the inbox, with roving focus and letter keys that only work with focus in the list; `a` and `r` act on the focused row only. |
| FB80 | **A server-side decision of a filter**: an overview with a token of the exact set and its versions, a decision of what is still pending in that set, tallies, a batch id in the audit, a repeat that reports, an upper bound, and `RequireIndividualDecision` for tools that are never decided in bulk. |
| FB81 | **`compact` for the board**, with a folded description, a two-line result preview and an `openInbox` output on the count of open proposals. |
| FB82 | **Decision times and a view per kind of action across agents, plus running a routine kind without approval**: the administration marks candidates from the numbers; a tool allows it in code, and an administrator switches it on for an agent as data, first in shadow (it records what would have run without approval and still asks), then live, with every execution audited as without approval. |
| FB83 | **Done** in f7402f69. |
| FB84 | **The selection follows the list**, and "Approve selected" only sends ids of the shown proposals. |
| FB85 | **Train 33 with four lanes**: QB (FB78, FB80), QU (FB79, FB81, FB84; merges after QB), DS (the numbers and the view of FB82) and AE (running without approval; merges after DS). |

## Rules for all of it

- Every change is opt-in or compatible unless its choice says otherwise. A breaking change gets a
  `Breaking change | Required action` row.
- Every change follows the repository rules:
  - code and migrations for SQL Server and PostgreSQL where persistence changes;
  - tests with authorization negatives;
  - a release-notes section per release unit;
  - guidance and consumer guide updates;
  - an executable sample case, extending the existing case where one covers the surface;
  - the public API snapshot for a public change;
  - English and Dutch texts and the Angular lifecycle extension point rules for the components.
- A decision never skips a proposal's own rules: rights, four-eyes, the approver rules, the tool's approve policy,
  revalidation and the stale guards stay per proposal, at decision time.
- Nothing names the consuming application.

## Choices

Each choice lists the options that were weighed. "(advice)" marks the recommendation.

### A. The queue

#### FB78. Counting and filtering by kind of action

`GET approvals/inbox` filters on `subjectType`, `agentId`, `divisionId`, `runId` and `status`, pages by at most 100,
and can group by object and run. It already returns the total of the filtered set and, grouped, the totals per
group; what it lacks is a filter on the kind of action (`toolId`) or the expiry, a sort, and counts per kind. Its
visibility query runs in SQL, and `ToolId` and `ExpiresAt` are columns of the approval row, so counting per kind is
cheap and exactly what the list shows. The listing does not apply four-eyes or the approver rules, which are only
checked when deciding, so a count means "listed", not "decidable". The name a card shows for a kind of action is
the presentation stored with each proposal (`ToolDisplayName`, per culture), with the tool id as fallback.

- **(a) A summary next to the inbox, two filters and a sort (advice).**
  - `GET approvals/inbox/summary` counts with the same filter and visibility: per agent, per kind of action (with
    the name the cards show), per object type, and how many expire within 24 hours.
  - The inbox takes `toolId` (one or more) and `expiresBefore`, and a sort on `expiresAt` or `createdAt`.
  - `NhAssistantAgentInbox` gets the same inputs, so a host can build its filter on them; the counts are named
    "listed", and the guidance says that deciding still checks each proposal.
  - No migration unless a measurement asks for an index.
- **(b) Only the `toolId` filter.** This works, but without counts nobody sees where the piles are.
- **(c) Nothing in NewHeap.** An application then fetches every page to count; that does not scale.

#### FB79. A compact list with a keyboard

`nh-assistant-agent-proposals` renders each proposal as a full card: header with badges, the "Proposed by a run"
line, summary, fields, technical details, meta line, feedback and buttons. There is no density option and no
keyboard handling; the library's only keyboard patterns are the pickers and the admin tabs. Approve and reject are
one click each, and the rule keeps rejecting at one click, with feedback asked afterwards.

- **(a) `density: 'compact'` with a keyboard that only works with focus in the list (advice).**
  - One row per proposal: object, kind of action, the summary on one line, agent and expiry, plus the markers that
    must not disappear (stale, waiting for someone else, consultation, grant source). Expanding in place shows the
    existing card.
  - Roving focus over the rows with the arrow keys, Home and End; Enter expands and Space selects.
  - The letters `j`, `k`, `x`, `a`, `r` and `?` work only while the focus is in the list and never in a field.
    `a` and `r` act on the focused row, never on the selection. `r` rejects at once and then offers the feedback,
    as a click does, and `?` lists the keys.
  - The inbox passes the density through. The comfortable cards stay the default.
- **(b) Compact rows with arrows, Enter and Space only.** Safer for screen readers, but slower than the letters the
  application asks for.
- **(c) Only collapsible cards.** Shorter, but it remains clicking.

#### FB80. Deciding a whole filter, after an overview

"Approve selected" sends the selected ids of the shown page to `POST approvals/decisions`, which takes 1 to 50 ids
and decides them one by one, each with all its rules. The bulk path passes no version of the proposal the person
saw, and the audit records a bulk decision exactly like a single one. No tool can opt out of being decided in bulk.

- **(a) A server-side decision of the filter, with an overview and a token (advice).**
  - An overview call returns, for a filter, the count, the counts per kind of action and per object type, and a
    token of the exact set, with the versions.
  - The decision call takes the token, the decision and an optional reason. It decides only the proposals of that
    set that are still pending, never one that arrived later.
  - Each proposal keeps its own rules. The result counts what succeeded, what was refused and what was already
    stale.
  - The confirmation names what happens, for example "212 times Add office task, on 38 projects".
  - The audit events carry a batch id, and a retry of the same token reports instead of deciding again.
  - A tool can say that it is never decided in bulk (`RequireIndividualDecision`), for something that cannot be
    undone, such as a message to an external party; its proposals stay out of the set and the overview says so.
  - The set has an upper bound.
- **(b) The client pages through the filter in chunks of 50**, after the same overview, with the versions. Smaller,
  but a closed tab leaves half a decision, and every other client has to do it again.
- **(c) Only per page, as now.**

#### FB84. A selection that outlives its page (found during the check)

The selection of `nh-assistant-agent-proposals` is a set of ids that nothing clears when the shown proposals change,
and the inbox keeps the same list when it pages. "Approve selected" sends the whole set, so proposals selected on
page 1 are decided from page 2 without being shown.

- **(a) The selection follows the list (advice).** It drops what the list no longer shows, and "Approve selected"
  only sends ids of the shown proposals; a spec pages with a selection.
- **(b) Clear the selection on every change of the list.** Simpler, but a refresh of the same page then loses a
  selection the person is still working on.

### B. The board

#### FB81. A compact board

`nh-assistant-agent-board` shows per agent a tile with the description and the "works on" line, a preview of the
last result (300 characters, cut off at 12em), buttons, and a count of open proposals. "Show proposals" opens a full
inbox in the tile. The library reads no route parameters; a host maps its own URL to the inbox's inputs.

- **(a) `compact` with a way to the host's inbox (advice).**
  - The description folds away, the result preview shows two lines with "More", which opens the existing result.
  - The count of open proposals becomes a button that emits `openInbox` with the agent and division, so the host
    routes to its filtered inbox; without a listener the tile keeps today's inline inbox.
- **(b) Only leave out the proposals.** Shorter, but the description and the result still make a wall.

### C. Insight

#### FB82. Seeing what is routine

`GET admin/agents/{id}/feedback/summary` already counts, per agent and per tool, what was approved, rejected,
expired and withdrawn over a period (30 days by default), with the rejection rate and the feedback categories. It
has no time to a decision and no view across agents. "Approved with changes" does not exist: a person can only
approve or reject a proposal as it is.

- **(a) Time to a decision, and a view per kind of action across agents (advice).**
  - The summary gets the median and the 90th percentile of the time from proposal to decision, computed per
    provider.
  - The administration gets a view per kind of action over all agents: counts, rejection rate and decision times.
    A kind that is almost always approved quickly stands out.
  - Changes nothing about how proposals are decided.
- **(b) Only the decision times in the existing per-agent summary.**
- **(c) A proposal to execute such a kind automatically.** That touches the trust in the agents; first the numbers,
  then a decision. Lars chose this one, and then how a kind runs without approval:
  - **(c1) A suggestion, switched on in code with two keys** (the tool allows it, the agent registration switches
    it on). Fits "grants never become data".
  - **(c2) A switch in the administration, first in shadow (chosen).** The tool allows it in code
    (`AllowWithoutApproval`); an administrator then switches it on for an agent as data, audited, first in shadow
    and then live. It works without a deployment, but a trust decision becomes data, so the guidance names it as
    the one exception to that rule and what the tool's code key must weigh.
  - **(c3) Only the suggestion,** with guidance on an application's own effect policy.

### D. Done and delivery

#### FB83. The picker after "+"

Fixed in f7402f69 before this plan: `startNewConversation` chooses among the agents of a new chat right away, through
`selectAgentOfChat`, which waits while the agents of a subject load. The application's suggestion,
`selectInitialAgent(this.chatAgents())`, would have chosen from an empty list during that load. Released with the
next `@newheap/platform-ai-chat`.

#### FB85. Train

- **(a) One train with four lanes (advice; the fourth lane came with FB82's choice).**
  - QB: FB78 and FB80, from the store to the inbox component, the API service and the mock.
  - QU: FB79, FB81 and FB84, in the proposals list and the board.
  - DS: the numbers and the view per kind of action of FB82, with the candidates.
  - AE: running a kind without approval: the tool's key, the switch as data with its migration on both
    providers, shadow, live and audit, and the switch in DS's view.

  QB and QU both touch the inbox component; QU merges after QB, and AE after DS. The full matrix runs once.
- **(b) FB84 first as a fix of its own, then the train.** It ships the bug fix sooner at the cost of a second
  verification round.

## Lane choices

| # | Choice | Status |
|---|---|---|
| DS1 | FB82 decision times as a bounded load, not `percentile_cont`: per group the newest 1,000 decisions (two timestamps each) through one provider-neutral LINQ query (`CROSS APPLY` on SQL Server, `JOIN LATERAL` on PostgreSQL), with linear interpolation as `percentile_cont` computes it and `sampled` when a group has more. SQL Server's `PERCENTILE_CONT` is only a window function, and EF Core translates neither provider's percentile nor `DateTimeOffset` subtraction, so `percentile_cont` would need raw SQL per provider with every filter duplicated. | kept by Lars (2026-11-02) |
| DS2 | No index and no migration: the view per kind of action reads the period's approvals by `CreatedAt` as the existing summary does; an index follows when a measurement asks for it, as FB78's rule says. | kept by Lars (2026-11-02) |
| DS3 | The view is `GET admin/proposal-kinds` (outside `agents/`, so it never collides with an agent id) with the summary's period rules and the filters `agentId` and `toolId`; an unknown agent lists nothing instead of 404. | kept by Lars (2026-11-02) |
| DS4 | The thresholds are server options (`NhAssistantRoutineCandidateOptions`, `NewHeap:AI:Assistant:RoutineCandidates` or `ConfigureRoutineCandidates`), returned with the view so it explains them; values outside their ranges count as the nearest bound, as the MCP options do. The quiet period counts back from the period's end; "stale or failed after approval" counts proposals that closed as stale at execution and failed executions, not uncertain ones. Shadow proposals, expiries, withdrawals, invalidations and decisions of agents or the platform never count as decisions. | kept by Lars (2026-11-02) |
| DS5 | A candidate is per agent and tool, because AE's switch is; a kind shows "n of m agents". Kinds with a candidate open on load, and a check box shows only those. | kept by Lars (2026-11-02) |
| DS6 | A kind's name is the presentation of its newest proposal in the period, in the caller's UI culture, with the tool id as fallback. | kept by Lars (2026-11-02) |
| DS7 | The sample lowers only `MinimumDecisions` to 3, in `appsettings.Development.json` (the library keeps 50), so a development database shows a candidate after three approvals; its test host sets the same in code. | kept by Lars (2026-11-02) |
| DS8 | Decision times subtract `CreatedAt`, which the platform records on the system clock, from `DecidedAt`, which follows the application's clock. With the system clock both agree; an application clock that runs ahead adds its shift, which the guidance says. Moving `CreatedAt` to the application's clock is a behavior change outside this lane. | kept by Lars (2026-11-02); followed up the same day: proposals record `ProposedAt` on the application's clock (migrations `ApprovalProposedAt`), and decision times run from it |
| DS9 | The section is "Kinds of action" between Agents and Tools & connections; `NhAssistantAdminTab` gets `proposal-kinds`, recorded as a breaking row for hosts that switch over the tabs exhaustively. | kept by Lars (2026-11-02) |
| DS10 | The contract for AE: `NhAssistantRoutineCandidateDto` / `RoutineCandidate` (`agentId`, `toolId`, `numbers`, `candidate`, `unmetThresholds`); in the view, `candidateActions` (a template with `$implicit` the candidate and `kind`) and the row component `tr[nhAssistantRoutineCandidateRow]` with `withActions` and projected content; on the server `NhAssistantProposalKindReport` and its `UnmetThresholds`. | kept by Lars (2026-11-02) |
| DS11 | The mock API answers `admin/proposal-kinds` with a fixed example over its first agents (a candidate and a kind that misses three thresholds), since it records no decisions. | kept by Lars (2026-11-02) |
| AE1 | FB82 code key: `AllowWithoutApproval` only for `IdempotentMutation` and `Mutation` with an approval the platform decides (`PolicyControlled`, `Required`, `NotRequired`), `Agent` exposure and a named policy. Refused as well as `Destructive` and issuers: reads, `ExternalSideEffect` (it leaves the application, so it cannot be undone there), `ConsumerAuthoritative`, compositions and the script tool; MCP imports never set it. Checked by the generator (`NHAI019`), by `NhAiWithoutApproval.Validate` inside `NhAiToolCatalogAttestation.Validate` for runtime catalogs, and again by the invoker; the assistant does not scan every catalog at startup, because reading an ApiExplorer catalog before the pipeline is configured breaks it. | kept by Lars (2026-11-02) |
| AE2 | A switch lifts only the approval of the default effect policy (`approval-required`, `agent-accountable-approval-required`, `effect-requires-approval`); an application's own effect policy that asks with another code, and every scope that requires approval for all effects (consultation, rule turn, shadow run, a call for another object, a person's turn nobody typed), keep it. | kept by Lars (2026-11-02) |
| AE3 | It applies only to a run's own headless turn in the agent's live thread: not a request turn (a person waits and decides inline), not a tool the application executes (`ExecuteInApplication`), not a person's chat or own turn in the thread. A step of a composition or script gets the decision its tool would get as a direct call, as everywhere else. | kept by Lars (2026-11-02) |
| AE4 | Before lifting the approval the call must fit what its proposal would meet: the approval policy selector must not refuse, no open or recently rejected proposal for the same objects (the policy's `Duplicates` and `RejectionCooldown`), and the guards of the proposal, decision and execution stages must pass; a stale result or a guard that throws turns it back into the proposal, which reports why. The subject-version precondition is met by definition when the call runs at once. | kept by Lars (2026-11-02) |
| AE5 | A switch applies to the tool contract it was made for (its hash is stored with every version); a changed contract reads as off with `contractChanged` and must go through shadow again. | kept by Lars (2026-11-02) |
| AE6 | Live only from shadow, enforced on the server; the same mode again writes nothing; `expectedVersion` (0 before the first switch) guards concurrent changes; versions are append-only rows of one table with who, when (application clock) and why (the reason stays out of the audit); the rows cascade with the agent's row, so a deleted administrator agent loses its switches. | kept by Lars (2026-11-02) |
| AE7 | Switching a kind that the numbers do not mark as a candidate is allowed, shadow and live: the candidate is a suggestion with configurable thresholds, and the switch is the administrator's audited decision. The UI names the thresholds a kind misses before it goes live. | kept by Lars (2026-11-02) |
| AE8 | The shadow mark is not shown to approvers, so it does not steer their decision; it lives on `AssistantApproval.WithoutApprovalVersion` and in the audit. DS's numbers count these proposals as ordinary decisions; a view that compares them by switch version is a follow-up. | kept by Lars (2026-11-02) |
| AE9 | Every call reads the switch from the database (no cache), so switching off applies to the next call on every host; the assistant's startup check refuses another `INhAiWithoutApprovalSwitches`, so only the audited switches decide. | kept by Lars (2026-11-02) |
| AE10 | A new public interface `INhAssistantWithoutApprovalSwitches` instead of new methods on `INhAssistantAgentControl`, which applications may implement; agents without work of their own (chat-only, shadow variants, no run configuration) cannot be switched. | kept by Lars (2026-11-02) |
| AE11 | A tool that declares FB80's `RequireIndividualDecision` can never run without approval: an effect a person must decide on its own card is no routine kind. The generator refuses both together (`NHAI019`), `NhAiWithoutApproval.IsAllowedBy` and `Validate` refuse it for runtime catalogs, the invoker never asks the switches for it and the administration lists no switch for it (`assistant-without-approval-not-allowed`). | kept by Lars (2026-11-02) |
| QB1 | FB78: `sort` takes `expiresAt` and `createdAt`, both earliest first, flat and grouped (groups then come by their first proposal of that time); there is no descending sort, and any other value answers `400`. | kept by Lars (2026-11-02) |
| QB2 | FB78: `expiresBefore` filters the decision deadline (`ExpiresAt`) in every status; the summary's `expiringSoon` counts proposals whose `ExpiresAt` is within 24 hours on the application's clock. | kept by Lars (2026-11-02) |
| QB3 | FB78: a kind of action is named by the presentation its newest proposal stored (in the caller's language), otherwise by its tool id; a summary names at most 50 kinds that way. The summary's agents carry the agent id only, clients name them from their own agent list. No index was added: no measurement asked for one. | kept by Lars (2026-11-02) |
| QB4 | FB80: the token is the id of a set stored server-side (`AssistantDecisionBatch`, migrations `BulkDecisionSets`), bound to the caller and the caller's tenant, valid for 15 minutes on the application's clock; another caller's token reads as unknown (`404`). It carries no data, so it is not signed; every item is checked again at the decision. | kept by Lars (2026-11-02) |
| QB5 | FB80: the set is decided in the request, not as a background operation, with a bound of 200; the loop ignores the request's cancellation so a closed page finishes the set, a claim with a 5-minute lease keeps a second request out (`409 assistant-bulk-decision-in-progress`), and a repeat after the lease takes over a set that stopped halfway, counting what this person already decided with the same version. Larger piles go in rounds (`remaining`). | kept by Lars (2026-11-02) |
| QB6 | FB80: the decision runs the inbox's own visibility again for the set; a proposal no longer listed is not decided: `stale` when it is no longer pending or expired, otherwise `refused` as unknown (`assistant-approval-not-found`). `stale` also covers a changed hash and a proposal closed as no longer applying; every other code is `refused`. A repeat that asks for the other decision reports the stored one. | kept by Lars (2026-11-02) |
| QB7 | FB80: `RequireIndividualDecision` is checked in the one decision for every decision with a batch id, so both bulk paths refuse such a proposal; it stays out of the contract hash, and the newest version of a tool decides. `POST approvals/decisions` gives its decisions one batch id per request as well (not stored), so the audit tells them from single decisions. | kept by Lars (2026-11-02) |
| QB8 | FB80: "Decide all N" is on by default (`decideAll`), shown when the filter lists more than one proposal, as an overview inside the inbox rather than a modal; no feedback question follows a bulk rejection. The board's inline inbox shows it too; lane QU may pass `[decideAll]="false"` in compact tiles. | kept by Lars (2026-11-02) |
| QB9 | FB80: `decideMany` also takes the shown proposals and then sends their hashes; the proposals list was left to lane QU, whose FB84 change of `decideSelected` should pass the shown proposals instead of ids so the library's list sends the versions. | kept by Lars (2026-11-02) |
| QB10 | FB80: in the sample, `projects.order-equipment` declares `RequireIndividualDecision`, so "Decide all" leaves equipment orders on the intake reviews page. | kept by Lars (2026-11-02) |
| QU1 | FB84: the selection is a linked signal of the shown pending proposals the caller may decide, closed cards the list keeps excluded; "Approve selected" and "Reject selected" send those proposals in the list's order, as proposals rather than ids, so `decideMany` sends the hashes the person saw (QB9). A proposal that leaves the list and comes back is not selected again. | kept by Lars (2026-11-02) |
| QU2 | FB79: a compact row is its checkbox plus one button that holds the list's single tab stop and expands the card in place (`aria-expanded`, `aria-controls`). The row checkboxes stay out of the tab order, because Space and `x` select, so Tab reaches the list once. The row names the object without its type and link; the expanded card has both. | kept by Lars (2026-11-02) |
| QU3 | FB79: the letters act only while the focus is inside a row, `?` anywhere in the list; never in an input, text area, select, editable content or the feedback form, never with Ctrl, Alt or Meta, and a held key does nothing more for `x`, `a` and `r`. Space on a row that cannot be selected keeps the button's own expansion. Each key action is read out through a polite status region. | kept by Lars (2026-11-02) |
| QU4 | FB79: `?` shows the keys as a panel above the rows, not a modal dialog; the focus moves to it and Escape or Close returns it to the row. A "Keyboard keys" button in the list's toolbar does the same for mouse users. | kept by Lars (2026-11-02) |
| QU5 | FB79: when a decision in a compact list, by key or by click in an expanded card, takes the focused row away, the row that takes its place gets the focus, but only when the focus was lost; focus the person moved elsewhere stays there. | kept by Lars (2026-11-02) |
| QU6 | FB81: `openInbox` works in both densities. While a host listens, the count of open proposals is the button and the tile has no inline inbox ("Show the proposals" goes); the board detects a listener through `outputFromObservable` over a subject (`observed`). Without a listener the count of a compact tile opens the tile's own inbox, and a comfortable tile stays as it was. | kept by Lars (2026-11-02) |
| QU7 | FB81: on a compact tile the description and the "works on" line fold together behind "About this agent"; "More" ("Less" while open) replaces "Show the result" and also shows for a run without a result. | kept by Lars (2026-11-02) |
| QU8 | Sample: the intake reviews page gets a "Compact list with keyboard keys" switch, off by default. The portal's Project management board is compact and routes `openInbox` to `/management/intake-reviews?agent=…&division=…`, where the inbox filters on that agent, records no visit and "Show all proposals" clears the filter; the playground's board stays comfortable. No new case: SPM-277, SPM-304 and SPM-310 are extended, SPM-389 stays unused. The intake reviews page counts its kinds of action (QB's summary) with the board's agent and division while they filter the inbox. | kept by Lars (2026-11-02) |
| QU9 | FB80 on the board (QB8): a compact tile's own inbox passes `[decideAll]="false"` and `density: 'compact'`, because a folded tile is a glance and deciding a whole agent's filter belongs in the full inbox, which the count reaches through `openInbox`; a comfortable tile's own inbox keeps "Decide all" and its cards, since there it is the board's only place to decide that agent's pile. | kept by Lars (2026-11-02) |
