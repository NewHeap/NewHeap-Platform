# Platform Feedback on 23.0.0 Plan

A consuming application checked NuGet 23.0.0, `@newheap/platform-common` 4.0.0,
`@newheap/platform-ai-chat` 0.22.0 and guidance 1.13.87 and reported what it still works around:
thirty findings on agent runs, chat channels, agents across applications, skills and packages,
the database read tool, background operations, Angular documents, mail and identity. Each finding was
checked against `main` at 8072ff1b (NuGet 24.0.0, `@newheap/platform-ai-chat` 0.23.0). Eleven were
already delivered before the version the application reported against, four of them in part; this plan
records how the application adopts those and one choice for each finding or part that is still open. It is a plan, not usage
documentation: the guidance rules, the release notes and the sample cases are authoritative once it is
built.

## Status

Decided by Lars on 2026-10-28: FB16 to FB40, every one as advised (option (a)). Train 26 is being built with lanes
DC, PD, PK, ID and ML; trains 27 to 29 follow.

## Already delivered

These need no code. FB16 tells the application how to adopt them.

| Finding | What exists on `main` | Since |
|---|---|---|
| A host that knows schedules but does not run them | `NhAssistantOptions.RunSchedules = false` (`NewHeap:AI:Assistant:RunSchedules`) registers no schedule in code and syncs no data schedule, so neither `NhAssistantScheduleRegistration` nor `NhAssistantDataScheduleSync` needs removing by type name. | 21.0.0 |
| A run's result for the application | `INhAssistantAgentRunLookup.FindRunAsync(runId, tenantId)` returns `NhAssistantAgentRunFacts` (status, failure, proposal count, summary, result); chain it after `FindByEventAsync`. | 22.0.0 (FB2 of the 21.0.0 plan) |
| Presentations drop silently | A dropped presentation is logged as a warning with presenter, tool, culture and broken rule (`too-many-fields`). A registration check is impossible because presenters build their output at run time; FB18 makes the check testable. | 21.0.0 |
| A service turn's person, channel and conversation | `NhAiInvocationContext.ConversationId` and `.Channel`, the public `INhAiTurnPerson`, `INhAssistantConversations` (create, get, cancel) and `INhAssistantConversationTurns.AnswerAsync`. The principal stays on `INhAiTurnPerson.User`, not on the invocation context. | 22.0.0 (FB5) |
| Reviewed tools of a server that cannot be reached | `INhAssistantMcpServers.UpdateToolAsync` stores reviewed settings for a tool no sync has seen (`AwaitingSync`), and the first sync applies them. | 22.0.0 (FB10) |
| The editor title of an agent in code | The title uses `agentDisplayName`, and the redesigned agent page names the agent in its own heading. A raw key means the host has no translation for the agent's `displayNameKey`. | 0.20.0 |
| Skills that name a text asset | `INhAssistantInstructionAssetUsage.ListAsync` lists the skill versions and agents that name an asset. FB31 adds the candidates. | 21.0.0 |
| A candidate for an agent's definition | `INhAssistantAgentDefinitions.PromoteShadowVariantAsync` (and `POST admin/agents/{id}/promote`, `AsPackageVersion`) promotes a data variant onto its live agent. An agent in code that starts in shadow is in shadow as a whole and takes the new definition directly. | 21.0.0, 22.0.0 (FB11) |
| Read an agent's parts without an export | `INhAssistantAgentPackages.ReadAsync(agentIds)` and `GetPackageAsync` read without an audit, server side. FB33 adds the HTTP route. | 21.0.0 |
| Failure metadata must respect storage limits | `NhBackgroundOperationRunner` replaces an oversized code with `handler-rejected` and an oversized message key with `background-operation.failed`, logs the text and keeps status and retry; provider theories cover both. FB16 asks the application for the column and stack it saw. | 21.0.0 |
| Sanitized Markdown and diagram documents | `NhAssistantDocumentComponent` with `NhAssistantDiagramRenderer`, sibling paths, link and heading outputs, from `@newheap/platform-ai-chat`. FB39 decides whether it moves. | 0.20.0 |

## Decisions

Each as advised (option (a)).

| # | Decision |
|---|---|
| FB16 | **A reply to the application** with the table above and how to adopt each delivered item. |
| FB17 | **`NhAssistantOptions.AgentWork`** switches agent work off while the endpoints and the administration serve; one internal gate; chat turns keep running. |
| FB18 | **`NhAssistantPresentationLimits.FindBrokenRule`** made public, so presenters can be unit-tested. |
| FB19 | **`INhAssistantApprovalPolicySelector`** chooses a registered policy per proposal from its tool, skill, trigger, subject and arguments. |
| FB20 | **`INhAssistantApproverDirectory`**, server side, lists and counts the people a policy admits for a subject, with a `Complete` flag. |
| FB21 | **`INhAssistantProposals.ProposeForAsync`** records a deferred proposal for a person without a run, through the governed path. |
| FB22 | **`DecisionCulture` on the hand-off and the execution report**, recorded for deferred decisions too. |
| FB23 | **`INhAssistantConversations.ListAsync`, `ListAgentsAsync` and `UploadAsync`**, sharing the endpoints' checks. |
| FB24 | **`INhAssistantFileRetention.ForgetConversationAsync`** after an archive, and uploads offered only where the agent's profiles may receive their classification. |
| FB25 | **An optional subject on a person's chat**, checked with the subject's access resolver on every turn; migrations on both providers. |
| FB26 | **The invocation, conversation, turn and channel on `NhAiProposalGuardContext`** at the proposal stage. |
| FB27 | **`NhAssistantPanelService.open({ conversationId?, agentId?, draft? })`** puts a draft in the composer without sending it. |
| FB28 | **The consultation chain across applications**, opt-in in `com.newheap/assistant-context`, continued only for a reviewed binding and only to tighten. |
| FB29 | **A request that answers in the asking conversation** (opt-in), with the asking conversation kept on the run; migrations on both providers. |
| FB30 | **A consultation that only takes requests** (`RequestsOnly()`, and a data flag). |
| FB31 | **No pass-through of imported tools**; the boundary stays and the guidance names the code-owned wrapper. |
| FB32 | **`INhAssistantInstructionAssetUsage.ProposeCandidatesAsync`** makes shadow candidates of the skills that name an older asset version. |
| FB33 | **Promotion keeps the links of parts a variant does not carry** when asked (`UncarriedParts = Relink`). |
| FB34 | **`POST admin/agent-packages/read` and `GET admin/agent-packages/{packageId}`** without the export audit, with Angular methods. |
| FB35 | **MySQL and MariaDB in the database read tool**, as their own train. |
| FB36 | **Inline table-valued functions on SQL Server as a profile opt-in**, schema-bound and dependency-checked; PostgreSQL stays strict as a recorded gap. |
| FB37 | **`NhSmtpSecurityMode`** (STARTTLS required, implicit TLS, none) on MailKit in `NewHeap.Platform.Common`; cancellation is no longer a dispatch error. |
| FB38 | **A null-safe active-membership predicate** in `NhUserManager` on UTC day bounds. |
| FB39 | **The document component stays in `@newheap/platform-ai-chat`** until an application without the assistant needs it. |
| FB40 | **Four trains**, 26 to 29, with the lanes below. |

## Rules for all of it

- Every change is opt-in or compatible unless its choice says otherwise. A breaking change gets a
  `Breaking change | Required action` row in `docs/release-notes/v-next.md`.
- Every change follows the repository rules: code and migrations for SQL Server and PostgreSQL where
  persistence changes; tests with authorization negatives; a release-notes section per release unit;
  guidance and consumer-guide updates; an executable sample case; English and Dutch texts for Angular parts.
- One lane per train creates migrations of a library, so model snapshots never conflict.
- Nothing names the consuming application.

## Choices

Each choice lists the options that were weighed. "(advice)" marks the recommendation, which Lars followed for all
of them.

### A. Adoption

#### FB16. What the application is told

- **(a) A short reply note for the application (advice)** with the delivered table, the setting or service
  that replaces each workaround, and a request for the column and stack of the failure-metadata finding.
  Written by Lars or, on his word, in the application's repository.
- **(b) Nothing**; the application finds them in the release notes.

### B. Runs and decisions

#### FB17. An administration that serves while agents do not work

`Enabled` switches endpoints and agent work together; about twelve scattered checks gate the work
(triggers, runs and executions, maintenance, application messages, turns, conversations, the hub). Per
agent there are pause, stop, switch-off and the kill switch.

- **(a) `NhAssistantOptions.AgentWork` (advice).** `NewHeap:AI:Assistant:AgentWork`, default true, counts only
  while `Enabled`. One internal gate replaces the scattered checks for triggers, schedules, runs, executions,
  consultations, hand-offs, follow-ups and application messages; refused work answers
  `assistant-agent-work-off` and approved proposals are held as for a stopped agent. Chat turns keep running:
  a person who chats is not agent work. The status and the administration report `agentWork`, and the agent
  list shows a banner.
- **(b) (a) plus a public admission hook** `INhAssistantAgentWorkAdmission` per trigger, run or execution.
- **(c) The hook only.**

#### FB18. Presentation limits that applications can test

- **(a) Public `NhAssistantPresentationLimits.FindBrokenRule(presentation)` (advice)**, the checker the resolver
  uses, so a presenter's unit test fails before production drops its output.
- **(b) (a) plus a strict mode** for Development that throws instead of dropping.

#### FB19. An approval policy chosen per proposal

The policy resolves at registration from the trigger, skill, tool selector or agent; `FourEyes` is a flag.

- **(a) `INhAssistantApprovalPolicySelector` (advice).** Registered with `UseApprovalPolicySelector<T>()`, it is
  asked after the code's resolution with the agent, tool, skill, trigger, subject, arguments and the resolved
  policy id, and returns a registered policy id or `null` for the resolved one. An unknown id refuses the
  proposal; the choice is kept in the proposal's policy copy, so the decision checks what was chosen. A
  selector has a time limit and no side effects.
- **(b) A conditional four-eyes rule** (`FourEyes(WhileOtherApproversExist)`), re-checked at the decision with
  the directory of FB20, failing closed when the directory is incomplete.
- **(c) Both.**

(a) answers "four eyes only while two people may approve" at proposal time with FB20; (b) weakens a control
at decision time and waits for evidence that (a) is not enough.

#### FB20. Who may approve a proposal

- **(a) `INhAssistantApproverDirectory`, server side (advice).** `ListAsync(policyId, subject, tenantId,
  excludeActorId, limit)`, `CountAsync` and `ListForProposalAsync(approvalId)` (which uses the proposal's
  policy copy and leaves out the initiator under four eyes) return `NhAssistantApproverList(Approvers,
  Complete)`. Candidates are the subject's members and the initiator; policy rules go through
  `INhAiPrincipalFactory`. `Complete` is false at the cap or when policy approvers outside the members cannot
  be listed. No endpoint: it lists people.
- **(b) (a) plus an administration endpoint.**

#### FB21. A deferred proposal on a person's behalf without a run

- **(a) `INhAssistantProposals.ProposeForAsync(NhAssistantProposalRequest)` (advice).** The person (as for the
  decision service), agent, tool, arguments, subject, optional policy, presentations per culture prepared by
  the calling host and an idempotency key. It takes the governed path: the tool contract and the person's
  rights, guards and keys, duplicates and the rejection cooldown, the policy copy; deferred only, the person
  is the initiator, so four eyes applies. A presentation over the limits is refused with an error, not
  dropped. The proposal waits in the agent's thread at the subject; a new audit kind records it.
- **(b) A helper that starts a rule-only run and waits for its proposal**: smaller, but it keeps the run,
  its latency and its worker dependency.

#### FB22. The decision culture on the hand-off

- **(a) `DecisionCulture` on `NhAssistantProposalHandOff` and the execution report receipt (advice)**, recorded
  for deferred decisions as well, from the decide endpoint's culture and an optional `Culture` on
  `NhAssistantDecidingPerson`, checked against the supported UI cultures. No migration; agent-thread posts
  keep the default culture.

### C. Chat channels

#### FB23. Lists and uploads for an application's channel

- **(a) `ListAsync`, `ListAgentsAsync` and `UploadAsync` on `INhAssistantConversations` (advice).** The
  endpoints' checks (participant role, agent policy, rules, media type, size) move into the internal
  uploads service so endpoint and service share them; the upload takes a re-readable stream and never logs
  a file name. A breaking row for code that implements or mocks the interface.
- **(b) A new interface next to it**, which breaks nothing but splits one concept over two services.

#### FB24. Uploads go with their conversation

- **(a) `INhAssistantFileRetention.ForgetConversationAsync` and classification-aware uploads (advice).** Called
  after an archive commits and from subject retention, idempotent, best effort with a warning; it never undoes
  the archive. `GET agents` offers uploads only when the agent's profile chain may receive the upload
  classification (today an offered upload can fail in the turn); that visible change gets a release row.
- **(b) (a) with an outbox** for reliable delivery, which needs a table on both providers.

#### FB25. A subject for a person's chat

- **(a) An optional subject on `POST conversations` and `NhAssistantNewConversation` (advice).** Checked with
  the subject's access resolver, the tenant and what the agent works on when created, and again on every
  turn, read and invitation, so revoked access takes effect. The conversation keeps the subject (columns and
  an index on both providers) and offers it to tools, the file source, guards and the briefing, isolated from
  the agent's threads. An agent setting offers it only for conversations with a subject;
  `GET agents?subjectType=&subjectId=` filters, and the Angular store creates one with a subject.
- **(b) Leave it to the application.**

#### FB26. A proposal guard that knows the call

- **(a) Init-only `Invocation`, `ConversationId`, `TurnId` and `Channel` on `NhAiProposalGuardContext` (advice)**,
  set at the proposal stage and `null` at later stages, outside the proposal hash. A release row for record
  equality; guards must not authorize on them at later stages.

#### FB27. A draft for a new conversation

- **(a) `open(target?: string | { conversationId?, agentId?, draft?, replaceDraft? })` and a store
  `setDraft(text, { replace })` (advice).** It never sends, respects what the person already typed unless
  `replaceDraft`, caps at `maxMessageChars`, and works for the drawer and `mode="page"`.

### D. Agents across applications

#### FB28. The consultation chain across applications

The chain and depth come from the in-process turn; an MCP call starts at depth zero with no asking agent.

- **(a) The chain in `com.newheap/assistant-context` (advice).** `NhAssistantMcpContextBinding.IncludeChain`
  (opt-in) adds `chain: { agents, consultationDepth, handOffDepth }` under the same key (version 1 stays);
  `ContinueMcpChain` trusts it only from a reviewed, authenticated client. The receiving side takes
  `max(local, inherited) + 1` and counts inherited agents as askers, so limits only tighten. It is loop
  protection between cooperating applications, not authorization.
- **(b) Leave it**: budgets, daily consultation limits and time limits bound a loop.

#### FB29. A request that answers where it was asked

- **(a) `AnswerRequests(NhAssistantRequestAnswerRoute.AskingConversation)` (advice)** on the consultation and its
  data ceiling. The run keeps the asking conversation and turn (columns on both providers); a completed run
  posts its answer as an application message readable by the initiator, with a link to the thread, once
  (idempotency key per run). Skipped for MCP callers, shadow runs and restricted results.
- **(b) Leave the notification as it is.**

#### FB30. A request without a consultation

- **(a) `RequestsOnly()` on the consultation builder and a `requestsOnly` flag on the data definition (advice)**:
  only `assistant.request.*` is generated, and consult-only settings refuse host start. The data flag needs a
  column on both providers.
- **(b) A separate `AllowRequests(subjectType, ...)` builder**: a cleaner name, a second set of policy,
  role, exposure and classification settings.

#### FB31. Passing an imported tool on

- **(a) Keep the boundary (advice).** An imported MCP tool is never exported again; the guidance names the
  code-owned `[NhAiTool]` wrapper that calls the remote server under the application's own review.
- **(b) A reviewed pass-through of one named read-only tool with a hop limit** in `_meta`: a governance decision
  with confused-deputy risk.

### E. Skills and packages

#### FB32. Candidates for a new asset version

- **(a) `ProposeCandidatesAsync(assetId, assetVersion, actorId, reason)` (advice)** saves a shadow candidate for
  every live data skill that names an older version, respects package locks and reports per skill; agents are
  listed only.
- **(b) Candidates made automatically** when an asset gets a new version.

#### FB33. Parts a promoted variant does not carry

- **(a) `UncarriedParts = Relink` on the promotion request (advice)**: parts the variant leaves out keep their
  locks and link to the new package version, reported as `kept-linked`; the default stays.
- **(b) Relink by default**, a behavior change.
- **(c) A variant carries its live agent's triggers by id**, with id and ownership questions.

#### FB34. Read an agent's parts over HTTP

- **(a) `POST admin/agent-packages/read` and `GET admin/agent-packages/{packageId}` (advice)** under the
  administration policy without the export audit, with `readAgentPackageParts` and `getInstalledAgentPackage`
  in the Angular admin API.

### F. Database read tool

#### FB35. MySQL and MariaDB

- **(a) A MySQL/MariaDB provider in `NewHeap.Platform.DatabaseRead.Tool` (advice, last train).** `MySqlConnector`,
  profile values `mysql` and `mariadb` with a server-flavor check, `START TRANSACTION READ ONLY`, the right
  statement and lock time limits per flavor, deny-by-default principal verification covering dynamic
  privileges, roles, mandatory roles and DEFINER objects, the comment and function rules of the query policy,
  `information_schema` evidence, and Testcontainers `mysql:8.4` and `mariadb:11.4` with a CI matrix job and a
  tool-local provider enum. NewHeap's EF libraries stay SQL Server and PostgreSQL, recorded as a `library-gap`.
- **(b) Not now**; the application keeps its structured reader.

#### FB36. Inline table-valued functions on SQL Server

- **(a) Profile opt-in `sqlServerFunctionPolicy: inline-table-valued-select` (advice).** Only `SELECT` on an
  inline TVF that is schema-bound, has no `EXECUTE AS` and depends only on tables and views in the same
  database that the principal may select directly; everything else stays rejected, with new check ids.
  PostgreSQL stays strict, recorded as a gap.
- **(b) Keep the strict policy** and document why.

### G. Mail and identity

#### FB37. An explicit SMTP security mode

- **(a) `NhSmtpSecurityMode` on MailKit in `NewHeap.Platform.Common` (advice).** `StartTlsRequired`, `ImplicitTls`,
  `None`; a missing mode follows `EnableSsl` as today. STARTTLS is required, never "when available";
  authentication only with a user; no switch that skips certificate validation; startup validation; never a
  plaintext retry. The dispatcher no longer turns cancellation into a dispatch error (a release row). Tests run
  a Mailpit container with TLS for both modes, a rejected certificate and authentication.
- **(b) An `INhMailTransport` seam** with MailKit in a package of its own: Common stays lean, one more release
  unit.

#### FB38. Division lockout on EF Core InMemory

- **(a) Compare instants on UTC day bounds (advice)**: `LockOutStartDateTime == null || >= tomorrowStart ||
  LockOutEndDateTime < todayStart`, without `.Value`. The provider theory gains a user without an active division
  or memberships and the day edges, with a test clock; an InMemory unit test is labelled non-relational. A
  release row: rows stored with a non-zero offset follow UTC days at the edges.

### H. Angular and delivery

#### FB39. Where the document component lives

- **(a) Keep it in `@newheap/platform-ai-chat` (advice)** and say so in the reply.
- **(b) Move it to `@newheap/platform-common/markdown`** as `NhMarkdownDocumentComponent` and `NhDiagramRenderer`,
  with optional `marked` and `dompurify` peers and the assistant names kept as aliases.

#### FB40. Trains

Merge trains as Lars works: lanes in worktrees with focused tests, the full matrix once per train on local
`main`, failures back to their lane, pushed only on Lars's word. Case ids are reserved per lane.

| Train | Lane | Choices | Migrations | Case ids |
|---|---|---|---|---|
| 26 | DC: decisions small | FB18, FB22, FB26 | none | SPM-347–349 |
| 26 | PD: panel draft | FB27 | none | SPM-350 |
| 26 | PK: packages | FB32, FB33, FB34 | none | SPM-351–353 |
| 26 | ID: identity | FB38 | none | SPM-354 |
| 26 | ML: mail | FB37 | none | SPM-355 |
| 27 | AP: approvals | FB19, FB20 | none | SPM-356–357 |
| 27 | CH: chat channels | FB23, FB24 | none | SPM-358–359 |
| 27 | RQ: requests | FB29, FB30, FB31 (guidance) | AI.Chat, both providers | SPM-360–362 |
| 28 | SB: chat subject | FB25 | AI.Chat, both providers | SPM-363 |
| 28 | PF: proposals for a person | FB21 | none | SPM-364 |
| 28 | AW: agent work | FB17 | none | SPM-365 |
| 28 | XC: chain across applications | FB28 | none | SPM-366 |
| 29 | DB: database read | FB35, FB36 | none | SPM-367–368 |

Train 26 holds the small, independent changes; 27 builds the approval and chat services that 28's proposals
and subjects use; AW waits for CH because both touch the conversation services; 29 stands alone for its
provider matrix. FB16 and FB39 are text and go with train 26.

## Lane choices

Recorded by each lane under its own heading while it builds; ids are the lane's code and a number (DC1, DC2, ...).

### Lane DC

### Lane PD

| # | Choice |
|---|---|
| PD1 | Built by lane PD (2026-10-28): **`open(target?: string \| NhAssistantPanelOpenOptions)` stays `void`; `conversationId` wins over `agentId`, and `agentId` always starts a new conversation (`selectAgent` and `startNewConversation`), also for the agent already chosen**, because the string form and its fire-and-forget calls stay as they are and "a new chat with that agent" means a fresh conversation; a host that must explain an unavailable agent checks `store.agents()` first, as "Test in chat" does; for Lars to review. |
| PD2 | Built by lane PD (2026-10-28): **the composer decides what the person typed**: it keeps its text when the person typed or changed it, or when it is a refused message put back, and a prepared draft replaces only an empty input, an earlier draft the person did not change, or anything with `replace`, because the typed text lives only in each composer (drawer and page) and the store cannot see it without wiring every keystroke out; so `setDraft` returns `true` when the draft waits for the composer, not whether it showed, and a draft kept out is dropped without a notice; for Lars to review. |
| PD3 | Built by lane PD (2026-10-28): **one signal, two shapes: `pendingDraft()` is `string \| NhAssistantComposerDraft \| null`, the composer's `restore` input takes both, and `restoredDraft()` keeps naming only a refused message**, because a string keeps its old meaning (the person's own words, replacing the input) and a custom layout still bound to `restoredDraft()` never takes a host's draft as the person's text; `setDraft` without `replace` keeps a refused message that waits, and `consumeRestoredDraft()` returns the text of either; for Lars to review. |
| PD4 | Built by lane PD (2026-10-28): **the draft is trimmed and cut at `maxMessageChars` UTF-16 code units without splitting a surrogate pair, trailing space dropped, no ellipsis**, because the store and composer count code units and the person sees and edits the cut text before sending; empty text returns `false`; for Lars to review. |
| PD5 | Built by lane PD (2026-10-28): **a draft is dropped rather than misplaced: when its conversation does not open, when the person may not use the agent and when the drawer closed before the status loaded; `setDraft` is synchronous and returns `false` while the assistant is not available**, because a brief meant for one agent or conversation must never be sent to another, and a synchronous call matches `selectAgent` (call it after `initialize()`); for Lars to review. |
| PD6 | Built by lane PD (2026-10-28): **`open()` stays the drawer's; on a page of its own (`mode="page"`) the host calls `store.setDraft` (with `selectAgent` and `startNewConversation` for another agent) and both composers bind `pendingDraft()`, where a draft waits until the next composer shows**, because the page and the drawer already share the store and a page-aware `open()` would change what the launcher does on a chat page; the alternative left open is an `open()` that targets a shown page panel; for Lars to review. |
| PD7 | Built by lane PD (2026-10-28): **a new case SPM-350 in the assistant lab ("Draft a status brief" with a "Replace what I typed" switch) with a translated English and Dutch brief and a scripted answer**, because the lab runs on the mock API with two agents, so it shows agent selection, keep and replace and the answer without the back-end, while the project page is a modal on the real API; SPM-253 keeps the page mode and SPM-248 the panel basics; for Lars to review. |

### Lane PK

### Lane ID

| ID | Choice |
|---|---|
| ID1 | Built by lane ID (2026-10-28): **`c.UserId == user.Id && (c.LockOutStartDateTime == null \|\| c.LockOutStartDateTime >= tomorrowStart \|\| c.LockOutEndDateTime < todayStart)`**, with `todayStart` the UTC midnight of the clock's day and `tomorrowStart` a day later, shared by the division and role queries; it keeps the start and end days inclusive and translates to `EXISTS (... IS NULL OR ... >= @tomorrowStart OR ... < @todayStart)` on SQL Server and PostgreSQL without client evaluation, and the old predicate on the same test clock passes the new edges on PostgreSQL and fails only SQL Server's row stored at +02:00; for Lars to review. |
| ID2 | Built by lane ID (2026-10-28): **the clock is `services.GetService<TimeProvider>() ?? TimeProvider.System`, resolved in the existing constructor from the manager's service provider**, so the public constructors stay as they are and a registered test clock decides the lockout day; `IsBlockedAsync` (account lockout, an instant comparison in memory) keeps the system clock, as it is outside FB38; for Lars to review. |
| ID3 | Built by lane ID (2026-10-28): **SPM-211 extended (`DivisionMembershipSamplesTests.ALockoutCountsInWholeUtcDaysOfTheApplicationClock`), SPM-354 left unused**, because the repository rule says to extend the existing case and the change is the same membership-lockout surface: the manager's lockout through today holds until the clock's next UTC day, and the security officer without an active division or memberships keeps the application permissions; for Lars to review. |
| ID4 | Built by lane ID (2026-10-28): **the InMemory test runs the claims and the sign-in's access token (`INhAuthenticationService.CreateToken`), not the complete password sign-in**, because the session's refresh-token clean-up uses `ExecuteDeleteAsync`, which InMemory cannot run; the provider theory signs the same user in completely on both providers. The reported exception did not reproduce here with the old predicate on EF Core InMemory 10.0.10 or 10.0.12: the compiled InMemory expression keeps the user filter first and checks for null before `.Value`, so the test guards the path rather than replaying the failure; for Lars to review. |
| ID5 | Built by lane ID (2026-10-28): **`DivisionProviderTestHost` gains an InMemory variant, an optional application clock and password sign-in with division claims**, so the provider theory, the InMemory test and the existing division tests share one composition; for Lars to review. |
| ID6 | Built by lane ID (2026-10-28): **one `NewHeap.Platform.AspNet.Common` release section with two `Breaking change \| Required action` rows**: rows stored with a non-zero offset follow the UTC day at the edges (SQL Server's `CONVERT(date, …)` used the stored offset), and a registered `TimeProvider` decides the day; for Lars to review. |
| ID7 | Built by lane ID (2026-10-28): **`nh-auth-permission-scopes` names the UTC day of the application's clock, warns against `.Value.Date` in lockout queries and asks for the day edges on both providers; `nh-reusable-test-helper-boundaries` says what the password sign-in cannot do on InMemory**, because that is where an application's InMemory sign-in test meets the limit; for Lars to review. |

### Lane ML
