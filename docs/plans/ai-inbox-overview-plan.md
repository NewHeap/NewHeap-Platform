# Approval Inbox Overview Plan

This document records how the approval inbox of `NewHeap.Platform.AI.Chat` and
`@newheap/platform-ai-chat` gives approvers an overview: filters on a division and a run, deciding the
proposals of one run together in its result, grouping by the parent of each proposal's subject, and
marking what is new since a person's last visit. It was written on 2026-10-22 on
`feature/inbox-overview`, on top of the [work on application objects](ai-agent-work-on-objects-plan.md)
(boards, K15 and K16) and the [proposal reads](ai-proposal-reads-plan.md).

## Status

Built on 2026-10-22 in one lane of a merge train. Lars decided the four parts below (IN1 to IN4); the
open design choices taken while building them are recorded as IB1 to IB12 for Lars to review. The
sample evidence is SPM-310.

| Part | Status |
| --- | --- |
| IN1 | Built: the inbox component's `divisionId` and `runId` inputs. |
| IN2 | Built: deciding a run's proposals together in `nh-assistant-run-result`. |
| IN3 | Built: grouping by parent subject, with the `ProposalParents` migrations. |
| IN4 | Built: "New" in the inbox, with inbox visits next to board visits. |

## Decided by Lars

- **IN1.** `nh-assistant-agent-inbox` takes `divisionId` and `runId` and passes them to `getInbox`;
  changing any filter input reloads from page 1.
- **IN2.** `nh-assistant-run-result` shows its proposals selectable with "Select all" for the
  proposals the reader may decide, using live decision rights; a reader without rights sees no
  check boxes. The decide-many flow, the feedback after a rejection and `holdClosedCards` behave as in
  the inbox. No separate link to the inbox.
- **IN3.** Grouping by parent subject: an optional parent on `NhAiSubjectInfo`, filled by the
  application's resolver and documented with subject resolvers; the parent's type, id and name are
  recorded on the proposal when it is made, so the inbox never calls a resolver per row; the inbox API
  returns proposals grouped by parent, then subject, then run, contiguous across pages, paged by items,
  with the total per group on the page; proposals without a parent group by their subject; grouping is
  opt-in (a query parameter and a component input); the component shows headers per parent group and
  subject (optionally per run); "Select group" selects only the loaded, visible pending items the
  reader may decide, shown clearly; it works with the `divisionId` filter.
- **IN4.** "New" in the inbox like on boards: the inbox remembers per person and scope (`global` or a
  division's scope key) when the person last looked, marks proposals that arrived since then and those
  that arrive live while the inbox is open; a visit endpoint next to the board's.

## Decisions for Lars to review

- **IB1. The parent's shape.** `NhAiSubjectParent(NhAiSubjectRef Subject, string? DisplayName)` on
  `NhAiSubjectInfo.Parent`. The parent is a label only: the platform never resolves it, grants no access
  through it and does not require its type to be registered; a registered type adds its name and link.
  One level only (no parent of a parent). A description that names the subject as its own parent is
  refused as an invalid description. The display name is kept to 200 characters
  (`NhAiSubjectParent.MaxDisplayNameLength`). Everyone who sees a proposal reads the parent's name, so
  the application gives only a name the subject's readers may read (documented on the type and in the
  guidance).
- **IB2. Where the snapshot comes from.** The proposal records the parent of the subject it waits at
  as the turn described it: the run's subject, or for a proposal placed at another object that object's
  description. Proposals made before the migration have no parent and group by their subject.
- **IB3. Grouping is opt-in, the flat inbox is the default.** `GET approvals/inbox?grouped=true` and the
  component input `grouped` (default `false`). Without it the order (expiry, then id) and the response
  stay as they were, apart from the new optional fields.
- **IB4. The grouped order.** Groups by their first expiry, so the most urgent group comes first; a
  group's own subject first, then its other subjects by their first expiry; the proposals of one run
  together (runs without an id last, explicitly on both providers), then by expiry and id. Ties between
  groups and subjects fall back to scope key, type and id. Runs within a subject come in id order, which
  is stable per provider but not the same on SQL Server and PostgreSQL; tests assert contiguity, not
  the order of runs.
- **IB5. Scope keys in the group key.** A group is keyed by the thread's scope key, type and id, so equal
  ids of different tenants or divisions never meet in one group.
- **IB6. What the page tells per group.** `Groups` lists, per group and per subject on the page, `Count`
  on the page, `Total` in the inbox with its filters, and `ContinuesFromPreviousPage` and
  `ContinuesOnNextPage`, found by reading one proposal before and after the page. The items stay one flat
  list in group order, so clients that ignore `Groups` keep working. The group's name is its own
  subject's name when it has proposals in the group, otherwise the parent name of its newest proposal.
- **IB7. "Select group" is per parent group only**, selects the group's pending proposals on the page
  that the reader may decide, and says "N of M shown". Subjects show "N of M shown" when they continue
  elsewhere but have no select of their own; "Select all" stays for the page.
- **IB8. The sample's parent is the project's division, not a task's project.** The sample's agents work
  on projects only; there is no task subject type, and adding one would need a new agent on tasks. A
  project belongs to one division, so the project resolver names the division as its parent with the
  sample-owned, unregistered type `division`. The library tests prove the task-to-project case.
- **IB9. Inbox visits reuse the board visits.** They are stored in `AssistantBoardVisit` under the
  reserved group id `nh-inbox`; no new table. `AddAgentGroup` now refuses every group id that starts
  with `nh-` (a breaking change with a release note), and the run maintenance removes inbox visits by
  age only (`BoardVisitRetention`), never as the visits of an unregistered group.
- **IB10. The inbox's visit scope and its check.** The scope is `global`, or the division scope key of
  the `divisionId` filter; other filters (agent, subject type, run) do not change the scope.
  `POST approvals/inbox/visit?divisionId=` accepts a division the application lists and the caller works
  in (`INhDivisionAccess`), as boards do; otherwise `404 assistant-subject-not-found` (no new error code)
  and nothing is stored. Reading the inbox with any `divisionId` needs no check, because it reads only the
  caller's own visit.
- **IB11. New marking in the client.** Like the board: before the first visit everything is new; what
  was new when the inbox was opened stays new while it is shown, on every page (the client compares
  with the visit before it opened), and proposals that arrive live are new through the server's
  `NewApprovalIds`. `markNew` (default `true`) shows it and `recordVisit` (default `true`) records the
  visit once per scope after the first read. The board's per-agent inbox passes `false` for both, because
  one agent's proposals are no visit of the whole inbox and the tile already marks what is new.
- **IB12. Live decision rights in the run result** come from one `POST approvals/lookup` of the run's
  pending proposals (in batches of 100), the same live check as the subject's work, instead of a new
  field on the run result. A failed lookup leaves nothing decidable; the server checks every decision
  again anyway.

## Not done

- Selecting per subject or per run, and grouping the board's per-agent inbox.
- A second parent level (for example a portfolio above a project).
- A full sample flow with tasks as subjects (see IB8).
