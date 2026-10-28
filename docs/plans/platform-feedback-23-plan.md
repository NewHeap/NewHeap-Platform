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
DC, PD, PK, ID and ML; trains 27 to 29 follow. Later that day Lars added two items to train 26 before it is pushed:
FB28, which the application had since worked out in detail, moves forward from train 28 (lane XC), and FB41, an
unsent message that a closed drawer loses, was found in train 26's browser check (lane UT). FB42, signing external
MCP clients in, came back onto the application's open list after this plan was written; Lars took it into train 26
as advised (lane MS).

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
| FB41 | **The composer keeps a person's unsent message** when the drawer closes, decided by Lars on 2026-10-28. |
| FB42 | **The MCP exposure serves its protected-resource metadata and challenge and checks the audience**, so any MCP client that follows the MCP authorization flow signs in through the host's authorization server, decided by Lars on 2026-10-28. |

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

The application then said what it needs, and option (a) holds all of it:

- Each agent in the chain is qualified by its application, for example the issuer or resource the caller
  authenticated with, because two applications may both have an agent with the same id, and that is not a loop.
- A chain from a caller the receiving side did not review is dropped, not refused, so an ordinary MCP client
  keeps working.
- The inherited chain and depth seed the consultation scope. `MayOffer` and the depth check then apply as in
  one host: a loop is refused with `assistant-consult-depth-exceeded`, and `MaxConsultationDepth` bounds the
  whole path.
- The agent's budget, `MaxConsultationsPerActorPerDay` and the call's deadline stay the bound for a caller that
  sends nothing or a forged chain.
- Proved with two hosts:
  - A consults B, B consults A: refused, and no run starts;
  - the same call from a caller that was not reviewed: the chain is ignored and the rules within one host still apply;
  - a forged shallow depth: bounded by the daily limit;
  - two applications with the same agent id: they do not refuse each other.

#### FB41. A closed drawer loses the unsent message

Train 26's browser check found that closing the assistant drawer dropped the text in its message box: the
composer kept it in its own state, which ends with the drawer. A draft (FB27) then never sees what the person
typed before.

- **(a) The store keeps the unsent message per conversation (advice, decided by Lars)**:
  - The message is kept in memory only, never in browser storage.
  - It is restored when the drawer or the page opens that conversation again.
  - It is cleared when the message is sent and when the person signs out.
  - The drawer and `mode="page"` share it.
  - A draft respects it as typed text.
- **(b) Keep the composer alive while the drawer is closed**: this holds the text, but also keeps a hidden
  component and its subscriptions running.

#### FB42. Signing external MCP clients in

`WithNewHeapPlatformAITools` exposes tools over MCP, but leaves OAuth to the host. An MCP client that follows
the MCP authorization flow (Claude, ChatGPT, Codex or any other) cannot find the host's authorization server
from the endpoint. So the application serves these itself:

- the protected-resource metadata (RFC 9728);
- the `WWW-Authenticate` challenge with `resource_metadata`;
- the audience check of a resource indicator (RFC 8707).

NewHeap has only the client half: `NhAssistantMcpDiscovery` reads another server's challenge and metadata, and
`NhAiTestAuthorizationServer` fakes an authorization server for tests.

- **(a) The server half on the MCP exposure (advice, decided by Lars).** The option takes a configured public
  resource URL, the authorization servers and the scopes. With it, the MCP exposure:
  - serves the metadata, at the path-specific well-known address and at the root;
  - answers a request without a valid token with `401` and the challenge, and with `error="invalid_token"` when a
    token was sent;
  - accepts only tokens whose audience is that resource.

  The authorization server, client registration and consent stay the host's. Nothing is specific to one client.
  It builds on what the MCP SDK offers, where that fits.
- **(b) An authorization server in NewHeap too**: a much larger security surface, which every host already has
  in its own identity provider.
- **(c) Leave it to the application**: every consumer that exposes tools writes the same server half again.

Follow-up of lane MS, taken into train A of `ai-tool-composition-plan.md` by Lars on 2026-10-28 (lane MD): `NhAssistantMcpDiscovery` probes a remote MCP server with GET to read its challenge, but a stateless server answers GET with 405. The assistant then misses the challenge's scopes and relies only on `scopes_supported` in the metadata. The probe should use an MCP `initialize` POST, or fall back to it after a 405.

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
| 26 | XC: chain across applications (moved from 28) | FB28 | none | SPM-366 |
| 26 | UT: unsent text | FB41 | none | SPM-369 |
| 26 | MS: MCP sign-in | FB42 | none | SPM-370 |
| 27 | AP: approvals | FB19, FB20 | none | SPM-356–357 |
| 27 | CH: chat channels | FB23, FB24 | none | SPM-358–359 |
| 27 | RQ: requests | FB29, FB30, FB31 (guidance) | AI.Chat, both providers | SPM-360–362 |
| 28 | SB: chat subject | FB25 | AI.Chat, both providers | SPM-363 |
| 28 | PF: proposals for a person | FB21 | none | SPM-364 |
| 28 | AW: agent work | FB17 | none | SPM-365 |
| 29 | DB: database read | FB35, FB36 | none | SPM-367–368 |

Train 26 holds the small, independent changes; 27 builds the approval and chat services that 28's proposals
and subjects use; AW waits for CH because both touch the conversation services; 29 stands alone for its
provider matrix. FB16 and FB39 are text and go with train 26.

## Lane choices

Recorded by each lane under its own heading while it builds; ids are the lane's code and a number (DC1, DC2, ...).

### Lane DC

| # | Choice |
|---|---|
| DC1 | Built by lane DC (2026-10-28): **`NhAssistantPresentationLimits.FindBrokenRule(presentation)` is the only check, and the resolver's internal `BrokenRuleOf` is gone**, because one implementation keeps the tested check and the drop identical; a null presentation throws `ArgumentNullException`, and no separate display-name check is added because its caps are two public constants; for Lars to review. |
| DC2 | Built by lane DC (2026-10-28): **a decision culture is recorded by its exact supported UI culture, otherwise as the first supported culture of its language (`nl-BE` as `nl-NL`), and a culture of another language records none while the decision stands**, because the endpoints read `Accept-Language` the same way and the culture is information for the application, not a reason to lose a valid decision; for Lars to review. |
| DC3 | Built by lane DC (2026-10-28): **the decision endpoints record the request's current UI culture, single and bulk decisions alike, and rejections record it too**, because request localization already chose a supported culture there and the record says how the person decided, whatever they decided; for Lars to review. |
| DC4 | Built by lane DC (2026-10-28): **an inline approval in an agent thread now records the turn's culture too (it was the chat only), checked as in DC2, and a resolution keeps the culture of the approval**, because the hand-off of every inline approval should carry it and agent-thread posts read only the default culture anyway, while a resolver's culture would need a way to clear the old one; for Lars to review. |
| DC5 | Built by lane DC (2026-10-28): **`DecisionCulture` is an init property of the receipt as of the hand-off, not a positional parameter**, because the constructors stay compatible; record equality changes, hence the release row; for Lars to review. |
| DC6 | Built by lane DC (2026-10-28): **the guard context names the call for the proposal-stage check and for `GetKeysAsync` of the new proposal (both run when a call proposes), with `ConversationId` and `Channel` from the invocation (the turn's scope as fallback) and `TurnId` from the turn; the consultation's move to the subject's approvers (CP20) names none**, because no call proposes there and the stored call is an older one; for Lars to review. |
| DC7 | Built by lane DC (2026-10-28): **new cases SPM-347, SPM-348 and SPM-349 instead of extending SPM-246, SPM-314 and SPM-342**, because each adds its own executable path: a presenter unit test without a host (SPM-246 is a host test), the reply channel's culture reaching the order desk (crossing SPM-268 and SPM-314) and a channel-aware guard (crossing SPM-282 and SPM-332); for Lars to review. |
| DC8 | Built by lane DC (2026-10-28): **the sample's reply token carries the message's culture (`OfferAsync(..., CultureInfo messageCulture, ...)`, the old overload uses the current UI culture) and the order desk confirms each reported order to its approver in the receipt's culture through localized `SampleEquipmentOrderTexts`**, because the thread's summary must keep the default culture and the approver's own confirmation is where an application uses the culture; for Lars to review. |
| DC9 | Built by lane DC (2026-10-28): **the sample guard refuses only the status Archived from the `messaging-chat` channel, registered for `NhAiProposalGuardStages.Proposal` alone, with `NotApplicable("project-archive-in-portal")`**, because archiving asks what to keep, which the portal shows, and the channel is known only at that stage; for Lars to review. |

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

| # | Choice |
|---|---|
| PK1 | Built by lane PK (2026-10-28): **`ProposeCandidatesAsync(assetId, assetVersion, actorId, reason = null)` reads the new version's hash from the application's source (as a save checks it) and saves each candidate through `INhAssistantAgentConfiguration.SaveSkillAsync` with apply `shadow` on the live version, carrying the live version's other fields; one skill's failure never stops the others**, so locks, limits, audit and reload are the save's own and nothing new can bypass them; for Lars to review. |
| PK2 | Built by lane PK (2026-10-28): **outcomes `proposed`, `already-proposed` (the skill's candidate already names that version and hash: nothing is saved, so a repeated call is idempotent), `locked` (`assistant-package-locked` with the save's `instructions: locked`) and `refused` (the save's code and field errors); the default reason is `asset <id>@<version>`**, because a new save would otherwise discard and recreate the same candidate and a reason makes the history readable; for Lars to review. |
| PK3 | Built by lane PK (2026-10-28): **only live skills and agents on an older version are reported; skills already on the version or newer are left out and agents are listed, never saved**; the call fails with `assetId`/`assetVersion: invalid`, `reason: too-long`, `assistant-instructions-asset-not-found` (no source or no such version), `assistant-instructions-asset-unavailable` and `instructionsAsset: invalid`, the codes a save gives, so `ListAsync` stays the full picture and the call only does what it can do; for Lars to review. |
| PK4 | Built by lane PK (2026-10-28): **`NhAssistantUncarriedParts` is an enum (`Unlink` = 0 default, `Relink`) on the C# request and the strings `unlink`/`relink` on HTTP and in Angular; `Relink` without `AsPackageVersion` is `uncarriedParts: not-allowed`, an undefined value `uncarriedParts: invalid`**, because only a package version leaves linked parts behind; for Lars to review. |
| PK5 | Built by lane PK (2026-10-28): **a kept-linked part keeps its link's hash, locked fields and locked values and moves to the variant's package id and version, with `LinkedAt`/`LinkedBy` of the promotion; it covers links to the variant's package and to the package the live agent belonged to before**, so `ChangedLocally` keeps telling whether the part changed since its import (a local change is not forgiven by the promotion) and the next upgrade still overwrites only what it should; reported with `ChangedLocally` and `LockedFields` of the link; for Lars to review. |
| PK6 | Built by lane PK (2026-10-28): **`POST admin/agent-packages/read` answers `NhAssistantAgentPackageReadDto { agents }` (nulls written, as the other endpoint DTOs) and `GET admin/agent-packages/{packageId}` answers `NhAssistantInstalledAgentPackageDto` or the new code `404 assistant-package-not-found`, both documenting 401/403**, a typed wrapper that can grow, and a dedicated code instead of the generic `assistant-not-found` so a client can tell an unknown package from an unknown route; for Lars to review. |
| PK7 | Built by lane PK (2026-10-28): **cases SPM-351 (operations service proposes risk review version 2), SPM-352 (kickoff desk's hand-off stays linked with relink, next to SPM-344) and SPM-353 (an administrator compares a shipped package over HTTP, next to SPM-331)**, new ids rather than extending SPM-331/344 because each is a separate surface with its own evidence; the SPM-331 usage test now looks at live versions only, since SPM-351 leaves history on the shared host; for Lars to review. |

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

| # | Choice |
|---|---|
| ML1 | Built by lane ML (2026-10-28): **MailKit 4.18.2 (MIT), pinned centrally, with `System.Security.Cryptography.Pkcs` raised from 9.0.21 to 10.0.13**, because 4.18.2 is the first MailKit/MimeKit whose floors exclude Pkcs 10.0.0-10.0.12 (CVE-2026-84008), MailKit before 4.16.0 and MimeKit up to 4.15.0 carry advisories, and MimeKit's net10.0 floor would otherwise be an NU1605 downgrade in the projects that pin Pkcs directly. No licence or notice change: `THIRD-PARTY-NOTICES.md` lists only material copied into the source tree and `npm run release:test` checks no package list. Release-note sections for the common and media units; for Lars to review. |
| ML2 | Built by lane ML (2026-10-28): **MailKit's default certificate validation, revocation included**, because it is the safer default and there is no switch to skip validation; `System.Net.Mail` did not check revocation, so a release row asks for access to the authority's OCSP/CRL endpoints; for Lars to review. |
| ML3 | Built by lane ML (2026-10-28): **an `internal virtual CreateSmtpClient()` as the only seam**, overridden by the friend test assembly to trust its own test authority through a custom-root chain that still checks chain and name, because a public seam would put MailKit types in the public API and give consumers a way around validation; for Lars to review. |
| ML4 | Built by lane ML (2026-10-28): **an internal `IValidateOptions<MailServiceSettings>` with `ValidateOnStart`, registered by both `WithMail` and `AddNewHeapNotifications<TContext>`, plus a public `MailServiceSettings.ResolveSecurityMode()` that throws for an undefined mode**, because the library has no options validation of its own yet, the standard pattern stops the host, and settings built with `Options.Create` still cannot send in an undefined mode. Only an undefined mode is refused, so existing settings, also ones without mail, keep starting; for Lars to review. |
| ML5 | Built by lane ML (2026-10-28): **`SecurityMode` nullable rather than a default enum value**, because "not set" must stay distinguishable so `EnableSsl` decides as before; for Lars to review. |
| ML6 | Built by lane ML (2026-10-28): **the dispatcher rethrows only a cancellation of its own token**; an `OperationCanceledException` the caller did not request, such as a client time-out, stays a dispatch error, because only the caller's cancellation means "stopped, not failed"; for Lars to review. |
| ML7 | Built by lane ML (2026-10-28): **a failed or cancelled QUIT after the server accepted the message does not fail the delivery** (debug log with the exception type only), because reporting it as failed would send the message again; for Lars to review. |
| ML8 | Built by lane ML (2026-10-28): **three Mailpit servers (`axllent/mailpit:v1.31.6`), one container each per test process, started lazily**: STARTTLS offered but optional (so an untrusted certificate proves there is no plain-text retry), implicit TLS on its own SMTPS port, and plain text (so required STARTTLS proves it refuses a server without it). TLS is proven by the client's certificate callback, authentication by Mailpit's recorded user name and MailKit's `Authenticated` event, mid-connect cancellation by a loopback listener that never greets; for Lars to review. |
| ML9 | Built by lane ML (2026-10-28): **new case SPM-355 for the security mode, and SPM-088 extended with the cancellation**, because SPM-084 is about the sender while the mode is a new settings, validation and transport surface, and SPM-088 already owns the e-mail dispatcher's failure states. The sample registers mail through `SampleMailComposition`, names `"SecurityMode": "None"` for its development server on localhost:1025, and its test shows a deployment override and the refused `StartTlsWhenAvailable`; for Lars to review. |

### Lane MS

| # | Choice |
|---|---|
| MS1 | Built by lane MS (2026-10-28): **the metadata document is the MCP SDK's `ProtectedResourceMetadata`, serialized with `McpJsonUtilities`, and the scheme follows the SDK handler's pattern of serving the document as an `IAuthenticationRequestHandler`, but the SDK's `McpAuthenticationHandler` (`AddMcp`) is not used**, because its challenge carries neither `scope` nor `error="invalid_token"`, it serves only the path-specific address, it validates no token and is meant as the host's default challenge scheme, which would change the challenge of every endpoint of a host with its own sign-in, and `NewHeap.Platform.AI.AspNet.Common` would gain the `ModelContextProtocol.AspNetCore` dependency; no NuGet package is added, token validation uses IdentityModel through the existing JWT bearer dependency; for Lars to review. |
| MS2 | Built by lane MS (2026-10-28): **an overload `WithNewHeapPlatformAITools(Action<NhAiMcpExposureBuilder>)` with `UseProtectedResource(Action<NhAiMcpProtectedResourceOptions>)`, once per host, and an authorization policy the host names on `MapMcp(...).RequireAuthorization(NhAiMcpProtectedResourceDefaults.AuthorizationPolicy)`; the existing overload is unchanged, and the only startup check is that every `AcceptHostSchemes` scheme exists**, because the library cannot reach the endpoint `MapMcp` creates, an explicit policy leaves the host's other endpoints and policies alone, a builder leaves room for later exposure options, and a check that some endpoint carries the policy would misjudge hosts that map endpoints in other ways while a missing policy only leaves the host's own sign-in in place, never a weaker one; for Lars to review. |
| MS3 | Built by lane MS (2026-10-28): **JWT access tokens validated with the keys of each issuer's metadata, read in the order of the MCP authorization specification (RFC 8414, then OpenID Connect discovery, as `NhAssistantMcpDiscovery` reads them), with the exact issuer, asymmetric algorithms only, a required expiry, a clock skew of one minute by default and at most five, the system clock, and one refresh of the keys for an unknown key id; a token that is not a JWT of an authorization server of the resource goes only to `AcceptHostSchemes`, which then own its validation, so opaque tokens with introspection stay possible in a host scheme**, because that is what an MCP authorization server publishes and JWT bearer authentication does, and a token's lifetime is a security check like its keys, not a business deadline; for Lars to review. |
| MS4 | Built by lane MS (2026-10-28): **a token is accepted when one of its `aud` values names the resource in canonical form (lowercase scheme and host, no default port, no trailing slash, no query or fragment), the origin alone or another path is refused, and the metadata publishes the canonical form of the configured `Resource`**, because authorization servers echo the `resource` parameter as the client sent it and RFC 8707 compares resource identifiers, and both the assistant's discovery and the MCP SDK client compare the published `resource` with the URL they use; for Lars to review. |
| MS5 | Built by lane MS (2026-10-28): **behind a reverse proxy `Resource` is the public URL: the document is served at `/.well-known/oauth-protected-resource` followed by the public path, matched against the full path the app sees (path base included, case and a trailing slash ignored), and at the root; the address of the app's own route is not served when it differs, and the challenge always names the public address built from `Resource`, never one from the request's host or forwarded headers; the proxy forwards the public well-known path to the app unchanged (`MetadataAddressOf` names it)**, because RFC 9728 derives the address from the resource the client knows, and an address built from a forwarded host could send clients to another document; for Lars to review. |
| MS6 | Built by lane MS (2026-10-28): **the root document is always served, also for a resource with a path, and a host has one protected resource**, because clients fall back to the root and a host has one MCP exposure; an origin that would serve two protected resources from one host is a recorded limit; for Lars to review. |
| MS7 | Built by lane MS (2026-10-28): **the challenge is `Bearer resource_metadata="...", scope="<every supported scope>"`, with `error="invalid_token"` only when the request sent a bearer token and no `error_description`, the same for a token a host scheme refused; the scopes are published and asked for but not required, so there is no `insufficient_scope`, and `AddScopeCapability("scope", value, capability)` maps a scope to a capability when a tool needs one**, because RFC 6750 leaves out the error without credentials, a reason would tell a caller why its token failed, and the tools' own policies already decide what a person may do; for Lars to review. |
| MS8 | Built by lane MS (2026-10-28): **`AcceptHostSchemes` only sees a request without a token of an authorization server of the resource; such a token is never handed to another scheme, even one that would accept it, and the resource scheme authenticates only on endpoints that require its policy**, because a host scheme that trusts the same authorization server without an audience would otherwise accept a token for another resource, and a token for the MCP resource must sign in nowhere else, also when it is the host's only scheme and becomes its default; for Lars to review. |
| MS9 | Built by lane MS (2026-10-28): **the token is never passed on: a request feature marks the sign-in, `NhAiHttpContextCallerCredentialAccessor` returns no token for it, so an API bridge tool fails with `api-bridge-unauthenticated` and a `forward-user-token` server gets nothing; the token is not part of `NhAiMcpProtectedResourceTokenValidatedContext` and no token exchange is offered**, because a token whose audience is the MCP resource must not become a credential for the application's API or another server, and an exchange would make NewHeap a token service; for Lars to review. |
| MS10 | Built by lane MS (2026-10-28): **the person is the host's: `OnTokenValidated` replaces or refuses the principal, without it the token's claims sign in (for `UseAuthenticatedClaimsWithoutTenant(issuer)`), and the library maps no NewHeap user itself; the sample links an account through an Identity login with the issuer as login provider and the subject as key, checks `MaySignInAsync` and signs in with the account's current claims plus the granted scopes**, because how an authorization server's subject becomes an account is an identity decision of the application, and the issuer and subject pair is the stable identifier OpenID Connect gives; for Lars to review. |
| MS11 | Built by lane MS (2026-10-28): **the metadata document allows any origin (`Access-Control-Allow-Origin: *` unless the host's CORS already set one), while the CORS of the 401 challenge stays the host's**, because the document is public and carries no credentials, and browser-based clients read it; for Lars to review. |
| MS12 | Built by lane MS (2026-10-28): **a stateless exposure answers `GET` with 405 before authorization, as the SDK maps only `POST`, and no challenge is added for unmapped methods; a client that probes with `GET` finds the metadata at the well-known addresses**, because a challenge there would need middleware outside the endpoint and RFC 9728 lets every client fall back; noted for later: `NhAssistantMcpDiscovery` probes with `GET`, so it never reads a stateless server's challenge scopes and relies on `scopes_supported`; for Lars to review. |
| MS13 | Built by lane MS (2026-10-28): **library tests in `NewHeap.Platform.AI.Tests` use a plain `POST /mcp` endpoint that stands in for `MapMcp`; the MCP transport, the official SDK client's OAuth flow and the assistant's round trip live in `NewHeap.Platform.AI.Chat.Tests` next to `NhAiMcpHttpMessageFormatTests`, the round trip on PostgreSQL only; `NhAiTestAuthorizationServer.IssueAccessToken(subject, resource, scope, issuedAt, lifetime)` is a new overload**, because only that project references the SDK's ASP.NET transport, persistence does not change, and an expired token must not need waiting; for Lars to review. |
| MS14 | Built by lane MS (2026-10-28): **a new case SPM-370 rather than extending SPM-222 or SPM-299, configured by `SampleProjectManagement:McpSignIn` without a development default; Program.cs maps `/mcp` through `MapSampleMcp`, which keeps `RequireAuthorization()` without the section, and `AcceptHostSchemes` keeps the API's own NewHeap token; the test signs the division editor in with the official MCP SDK client, which checks state and issuer, and sends the active division header as the API's own clients do**, because the direction from clients outside into the API is its own surface with its own evidence and the sample has no authorization server of its own; for Lars to review. |
| MS15 | Built by lane MS (2026-10-28): **one release-note section for the common NuGet unit (`NewHeap.Platform.AI.AspNet.Common` and `NewHeap.Platform.AI.Test`) without a breaking row**, because nothing changes without the option and the caller credential accessor changes only for requests signed in through the new scheme; for Lars to review. |

### Lane XC

| # | Choice |
|---|---|
| XC1 | Built by lane XC (2026-10-28): **every application names itself once with `UseMcpChainApplication(applicationId)` and that name travels unchanged through the chain; `ContinueMcpChain(new NhAssistantMcpChainCaller(applicationId, issuer))` binds a reviewed caller to the name its calling agent must carry**, because a receiver can only recognize its own agents in a chain that others pass on when each application is named the same everywhere, while an issuer alone names neither side reliably (a shared login service, a person's delegated token); a binding with the chain or a reviewed caller without a name, and a reviewed caller under the application's own name, fail startup; for Lars to review. |
| XC2 | Built by lane XC (2026-10-28): **in the consultation scope an agent of this application keeps its id and another application's agent becomes `agentId@application`** (agent ids are dash-case and never contain `@`), so `MayOffer`, the depth check and `askers.Contains` work unchanged and two applications with an agent of the same id never refuse each other; on the wire each agent is `{ "application", "agentId" }`; for Lars to review. |
| XC3 | Built by lane XC (2026-10-28): **a reviewed caller is recognized by its authenticated principal: exactly one `iss` claim (`IssuerClaimType`) with the issuer and, optionally, exactly one `ClientClaimType` claim with `ClientId`; a client-specific registration wins over the issuer's other callers**, rather than by `NhAiInvocationContext.Issuer`, because that is set only when the host projects issuers with `UseAuthenticatedClaims`, and a consultation over MCP acts for a person whose delegated token carries the calling application's issuer; for Lars to review. |
| XC4 | Built by lane XC (2026-10-28): **a malformed chain of a reviewed caller is ignored like an unreviewed caller's, with a warning naming a fixed problem code (`version-unsupported`, `chain-invalid`, `depth-invalid`, `agents-invalid`, `agent-invalid`, `caller-mismatch`) and never the payload; a chain whose last agent is not of the caller's application is malformed; a well-formed large depth is believed; members a receiver does not know are left alone; at most `MaxChainAgents` (16) agents**, because a chain never refuses by its form, only by what it says, and a believed depth can only tighten; for Lars to review. |
| XC5 | Built by lane XC (2026-10-28): **no `handOffDepth` is sent**, because the consultation scope does not hold it (only the run row does) and no receiving rule would use it; the receiver leaves unknown members alone, so a later sender may add it; for Lars to review. |
| XC6 | Built by lane XC (2026-10-28): **with `IncludeChain` the binding also binds a call from a consultation turn, which the snapshot provider refuses: the binder checks the running call itself and sends `conversation` with the consulted agent's thread and the consultation as turn, and no snapshot; `NhAssistantMcpContext.BinderId` is public**, because a consultation turn offers no administrator-connected MCP tools, so the second hop of every chain is a code-owned import that names the binder; without `IncludeChain` nothing changes; for Lars to review. |
| XC7 | Built by lane XC (2026-10-28): **a request (`assistant.request.*`) from a reviewed caller whose chain has a depth above 0 is refused with `assistant-consult-depth-exceeded` before a run starts; at depth 0 the chain changes nothing for a request**, because one host never offers a request inside a consultation (`MayOffer`), so across applications it holds the same; for Lars to review. |
| XC8 | Built by lane XC (2026-10-28): **`AskingAgentId` stays `null` for an MCP call with a chain and `Depth` counts the chain; the documentation of `AssistantConsultation` and `NhAssistantConsultationDto` says so; no migration**, because the column names an agent of this application, which the administration shows as one, and the chain's agents are another application's; for Lars to review. |
| XC9 | Built by lane XC (2026-10-28): **the request metadata reaches the assistant through an internal ambient that `WithNewHeapPlatformAITools`' call handler sets around the tool call, not a public API**, because only the NewHeap export runs governed tools, and the metadata is what the caller sent, never identity; for Lars to review. |
| XC10 | Built by lane XC (2026-10-28): **new case SPM-366: `SampleConsultationChain` names the API and reviews a partner from configuration, the research binding of SPM-266 sends the chain once the API is named, and the intake reviewer's consultation is offered to MCP clients (`Exposure(Local \| Agent \| Mcp)`)**, because the receiving side only exists for a consultation that MCP clients see; the sample test calls the API's MCP endpoint as the partner (a continued chain, a refused loop, an ignored stranger), and the proofs with two, three and four hosts are library tests; for Lars to review. |
| XC11 | Built by lane XC (2026-10-28): **the library tests run four ASP.NET hosts in one class fixture on PostgreSQL, each importing its peers' consult tools through a catalog that stays empty until all hosts run, with a model per host that has one script for chat turns and one for consultation turns and a recorder of the context each host receives**, because applications must import each other, a consultation turn runs inside a chat turn of the same host, and no persistence changed, so SQL Server adds no evidence; it also proves the loop of three hosts (A asks B, B asks C, C asks B) that a correlation id would miss; for Lars to review. |

### Lane UT

| # | Choice |
|---|---|
| UT1 | Built by lane UT (2026-10-28): **the store keeps `NhAssistantUnsentMessage { text, prepared }` per conversation (`unsentMessage()`, `keepUnsentMessage(message)`), and the composer stays presentational with a new `unsent` input and `unsentChange` output that `nh-assistant-panel` binds; a composer without `unsent` keeps its text itself, as before**, because the composer has no store dependency and custom layouts keep working unchanged, one input and output let a custom layout keep the text too, `prepared` keeps PD's "an unchanged draft gives way" after the drawer opened again, and the text is kept untrimmed so spaces and the caret stay where the person left them; for Lars to review. |
| UT2 | Built by lane UT (2026-10-28): **a new chat's text is kept under the new-chat key and moves into the conversation created for it, by a send or by an upload, leaving the key free for the next new chat**, as the attachments of a new chat move, so the person keeps typing in the conversation they see; a host's own `store.send(text)` leaves the person's text alone and it moves with the created conversation; for Lars to review. |
| UT3 | Built by lane UT (2026-10-28): **the composer empties the kept message when it sends (`unsentChange(null)` before `send`), the store's `send` does not; a message the server refuses before its turn starts, or one whose new conversation could not be created, comes back as the person's own text of its conversation, also when another conversation is open meanwhile (where it was lost before)**, because a host's quick action or the thread's retry must never drop what the person typed, and a refusal belongs to its conversation; as with the string `restore`, the refused text replaces what the person typed in that conversation meanwhile, and a refused queued message keeps its failed place in the queue; for Lars to review. |
| UT4 | Built by lane UT (2026-10-28): **forgotten in the account reset (signing out through the access policy, another person through the state scope), as the queue and the attachments are, and with a deleted, left or removed conversation; never written to browser storage; no cap, and a session the store evicts keeps its text**, because the reset already covers both account changes and the text is only what a person typed and did not send; for Lars to review. |
| UT5 | Built by lane UT (2026-10-28): **`setDraft` now returns `false`, and puts nothing in `pendingDraft()`, when the open conversation's unsent message is the person's own text and `replace` is not set; PD's page spec expects `false` there instead of `true`**, because PD2's reason (the store could not see what the person typed) no longer holds, a draft kept out can then never land in another conversation's composer, and the composer still decides for a composer without `unsent`; `pendingDraft()`, `restore` and `restoredDraft()` stay as PD built them; for Lars to review. |
| UT6 | Built by lane UT (2026-10-28): **each conversation, and the new chat, shows its own unsent text, also after "New conversation" and in the conversation list, where the text used to stay in the composer; only choosing another agent in the composer, which starts a new chat, takes the text along (unless the new chat has text of its own), and `open({ agentId, draft })` starts the new chat before choosing the agent, so the old conversation keeps its text and the draft lands in the new chat's empty composer**, because the picker sits in the composer and the guidance already says choosing an agent preserves the draft; no `Breaking change` row, since no host has to act; for Lars to review. |
| UT7 | Built by lane UT (2026-10-28): **the composer handles `unsent` and `restore` in one effect, the kept message first, and each value once by identity**, so a draft that arrives together with another conversation's message is decided against the text it lands on and the composer's own reported text coming back is no change; the panel spec renders after typing, as a browser does after every input event, because switching conversations in the same task as typing would leave the textarea's `[value]` binding stale, as for PD's `restore`; for Lars to review. |
| UT8 | Built by lane UT (2026-10-28): **new case SPM-369 with an "Unsent message" card in the assistant lab (open and close the assistant, the kept text quoted up to 80 characters or a prepared brief named, English and Dutch) and the store state's length and `prepared`, with the library specs as evidence; the access switch shows signing out**, because the lab runs on the mock API with two agents and the drawer, while the portal's chat page (SPM-253) uses the real API in another scope, so the drawer and the page sharing the text is proven by the library spec; SPM-350 and SPM-248 stay as they are; for Lars to review. |

### Lane MD

| # | Choice |
|---|---|
| MD1 | Built by lane MD (2026-10-28): **the probe sends an MCP `initialize` POST first and a GET only after that POST answered `405`; any other answer is the probe's answer, as the GET's was**, because the POST is the request with which an MCP client starts over Streamable HTTP and every server must take it, while GET is optional and answers `405` on a stateless server and on every server of the 2026-07-28 revision, which has no sessions; the MCP authorization flow takes the scopes from the challenge of that first request, one request then suffices, and a server that checks a GET's session before its authorization gives no challenge to a GET at all. The GET after a `405` keeps HTTP+SSE endpoints, which `NhAssistantMcpTransports.Auto` still connects, in the same order as the SDK's auto-detection; for Lars to review. |
| MD2 | Built by lane MD (2026-10-28): **the probe's JSON-RPC message is `initialize` with protocol version `2025-11-25` and `clientInfo` `newheap-assistant`, not the `server/discover` with which the SDK client of the 2026-07-28 revision starts**, because a server decides its authorization on the HTTP request before it reads the method, so both get the same challenge, while a server before that revision may leave an unknown method unanswered (the SDK bounds its own discover probe with a timeout for that reason) and a server of every revision answers `initialize` at once, if only with an error; the request carries no credential, no cookie and none of the server's configured headers; for Lars to review. |
| MD3 | Built by lane MD (2026-10-28): **the probe keeps only the status and the headers and never reads a body, and a session that a server without authorization opened for the `initialize` is ended with `DELETE` (a visible-ASCII `Mcp-Session-Id` only; its answer is ignored)**, because discovery uses only the status and the challenge, an event stream that stays open (an unprotected HTTP+SSE endpoint's GET) then no longer holds the probe until the connect timeout and reports the server unreachable, and the `initialize` would otherwise leave an unused session on a stateful server until it expires; every probe request keeps the host guard of MCP endpoints, no redirects and its own connect timeout, as the OAuth HTTP client's other requests; for Lars to review. |
| MD4 | Built by lane MD (2026-10-28): **the lane MS round trip (`AssistantMcpProtectedResourceTests`) now asserts the challenge's scopes of the stateless protected NewHeap exposure, and a raw loopback server in `AssistantMcpOAuthTests` shows a stateless server, an HTTP+SSE endpoint, `405` on both methods and a server without authorization, and that no probe request carries a credential; PostgreSQL only**, because persistence does not change; both assertions fail against the GET probe, and the existing protected servers that also challenge a GET (`NhAiTestProtectedResource` in front of the SDK server) pass unchanged; for Lars to review. |
| MD5 | Built by lane MD (2026-10-28): **SPM-299 extended rather than a new case: the sample's protected planning server (`SampleMcpServer.StartProtectedAsync`, already stateless) now answers `GET` with `405` before any challenge, as a stateless server whose authorization belongs to its mapped `POST` endpoint does; the SPM-299 test checks that `405` and still asserts `challengedScopes`, so it needs the `initialize` probe; the registry outcome, the catalog and the evidence (`NhAssistantMcpOAuthHttp.cs`) say so. One sentence each in `nh-ai-assistant` (connecting OAuth MCP servers) and `nh-ai-mcp-tools` (which described the GET probe), and one release-note section for `NewHeap.Platform.AI.Chat.AspNet` without a breaking row**, because discovery of a stateless server is the surface SPM-299 already shows, the probe is internal, no public API changes and the discover result keeps its shape, while a server that answered `GET` with `405` only gains its challenge's scopes; the sample's other protected servers (SPM-299, SPM-300) connect over Streamable HTTP without a `GET` and pass unchanged; for Lars to review. |

### Lane AP

| # | Choice |
|---|---|
| AP1 | Built by lane AP (2026-10-28): **the selector is asked after code's resolution for every proposal of the governed path that has an approval policy: runs and rule runs, a person's messages in an agent's thread, consultations and the steps of compositions and scripts in those turns; a person's own chat with the assistant, whose proposals that person decides without a policy, does not ask it; `NhAssistantApprovalPolicySource` names `Run`, `Request` and `Consultation`**, because only those proposals have a policy copy that a decision checks, and a chat's decision never reads one; for Lars to review. |
| AP2 | Built by lane AP (2026-10-28): **the selector fails closed: an id that is not registered refuses the proposal with `assistant-approval-policy-unknown` (409), a selector that throws with `assistant-approval-policy-selector-failed` and one past its time limit with `assistant-approval-policy-selector-timed-out` (both 503); returning the resolved id counts as `null`, and the defaults `trigger-default` and `chat-default` can be chosen only as the resolved policy**, because a selector usually tightens, so falling back to the resolved policy could weaken a control, and "a registered policy id" excludes the built-in defaults; for Lars to review. |
| AP3 | Built by lane AP (2026-10-28): **a time limit of two seconds by default, at most thirty, set per registration (`UseApprovalPolicySelector<TSelector>(timeLimit)`), on the system clock, enforced with a cancelled token and with `WaitAsync`, so a selector that ignores its token is cut off too**, because the selector runs inside a turn that people wait for, the limit is an operational bound like a model timeout rather than a business deadline, and a test sets a short limit instead of waiting; for Lars to review. |
| AP4 | Built by lane AP (2026-10-28): **in a consultation the selector chooses right after `ResolveConsultation` and before the route, which adapts the chosen policy (the asker's inline lifetime cap, deferred at the subject, four eyes for the agent's rights); the proposal an asker leaves for the subject's approvers (CP20) asks the selector again as a new proposal at the subject, with the earlier proposal's arguments and no origin**, because the route decides who may decide and must see the chosen policy, and the moved proposal is a new proposal whose policy code resolves again; for Lars to review. |
| AP5 | Built by lane AP (2026-10-28): **`NhAssistantApprovalPolicySelection` carries, next to the brief's agent, tool, skill, trigger, subject, arguments and resolved id, the source, initiator, tenant, run and the step's `Origin`; the arguments are canonical as the proposal records them, the subject is where the proposal waits (the object a run placed it at), and no prompt, turn, conversation or channel is passed**, because "four eyes only while two people may approve" needs the initiator and the tenant for the directory, and the selector should see what approvers see; for Lars to review. |
| AP6 | Built by lane AP (2026-10-28): **a refusal records nothing: the call reads `not-executed` with the code and a result that tells the model not to retry, the content-free audit records `ToolInvoked` with the code, a rule run's `ProposeAsync` returns `Refused` with the code, a step fails with it, and a warning names the selector's type and, for an exception, its type only**, as the other refusals before a proposal (not placed, not needed) do; for Lars to review. |
| AP7 | Built by lane AP (2026-10-28): **`ListForProposalAsync(approvalId, tenantId, limit)` takes the caller's tenant, and `CountAsync` returns `NhAssistantApproverCount(Count, Complete)` instead of a list; all three take a `limit` of 1 to 200 (50 by default) and throw for one out of range**, because the brief's `ListForProposalAsync(approvalId)` would reach another tenant's proposal, which the decision service refuses, and a count needs no names; for Lars to review. |
| AP8 | Built by lane AP (2026-10-28): **the candidates are the members the subject's resolver lists now (never the member snapshot, at most 1,000) and, for a proposal, its initiator first; each is checked as the decision checks people, through a shared check in `NhAssistantApproverCheck`: subject and named roles live from the resolver, a policy rule against the principal `INhAiPrincipalFactory` rebuilds once per person and only when needed, the tool's approve policies and the agent's view policy of the whole application for a proposal, never an agent; `ListAsync` knows no initiator, so the initiator rule admits nobody there, the defaults `trigger-default` and `chat-default` are accepted, and a consultation's proposal the asker decides lists only the asker**, because the list must be exactly the people a decision would accept, which a test proves person by person; for Lars to review. |
| AP9 | Built by lane AP (2026-10-28): **`Complete` is `false` at the limit with candidates left, beyond 1,000 members, for any policy rule and for a subject whose resolver lists no members (the whole application); a resolver that gives roles to people it does not list is not detected and is documented**, because a policy rule may admit anyone in the application and only the resolver knows its members; for Lars to review. |
| AP10 | Built by lane AP (2026-10-28): **the sample's `SampleIntakeApprovalPolicySelector` reacts to the resolved policy `project-intake`, so it covers the intake reviewer and the planner, counts the approvers other than the initiator with `CountAsync(limit: 1)` and chooses `project-intake-sole-approver` only for a complete count of zero; Program.cs registers it, and its evidence runs in a host of its own derived from the agent run host, which keeps four eyes as before**, because the choice belongs to the policy, an incomplete answer must keep the stricter policy, and the existing run evidence relies on four eyes on projects whose creator is the only approver; for Lars to review. |
| AP11 | Built by lane AP (2026-10-28): **SPM-357 shows the directory in the approval reply channel (`OfferToApproversAsync`), which asks everyone `ListForProposalAsync` lists and still confirms each with `GetDecisionFactsAsync`, next to the selector's `CountAsync`; two new cases rather than extending SPM-268**, because the reply channel is where an application decides whom to ask, and the selector and the directory are separate surfaces with their own evidence; for Lars to review. |
| AP12 | Built by lane AP (2026-10-28): **the Angular library gets the three codes in `NH_ASSISTANT_SERVER_ERROR_CODES` with English and Dutch texts as tool-call results and as errors, and the mock API's status table, and no other change**, because the codes reach a person's tool call card and the leave-for-approvers decision, and the directory has no endpoint; for Lars to review. |

### Lane CH

| # | Choice |
|---|---|
| CH1 | Built by lane CH (2026-10-28): **the shared checks live in the internal `NhAssistantConversationUploads` of `NewHeap.Platform.AI.Chat.AspNet` (`PrepareAsync`: the agent's policy for everyone who uploads, the role in an agent's thread, a file source, the agent's rules; `StoreAsync`: name, type against content, size, scanner, file source through the core `NhAssistantUploads`), and the endpoint resolves the conversation without an agent check before it**, because the agent's policy needs ASP.NET authorization, which the core package does not reference; the order and codes of the endpoint stay as they were, and the multipart request (header, body limit, one file part) stays the endpoint's; for Lars to review. |
| CH2 | Built by lane CH (2026-10-28): **`UploadAsync` takes `NhAssistantConversationUpload(FileName, MediaType, Content)` with a readable, seekable stream, read from the caller's position for each step through a view that never disposes it and stops at the measured length; a stream that cannot seek throws `ArgumentException`**, because a channel's attachment is usually buffered already, a stream is what "re-readable" asks for, and a factory would let the content differ between the content check, the scanner and the file source; the caller keeps and disposes it; for Lars to review. |
| CH3 | Built by lane CH (2026-10-28): **`ListAsync(person, NhAssistantConversationPage? page = null, ...)` with a record `(Page = 1, ItemsPerPage = 20)` and the endpoint's bounds, and `ListAgentsAsync(person, options)` without a filter, both returning the endpoints' DTOs through one shared implementation (`NhAssistantPersonLists`)**, because a channel then lists exactly what the panel lists, and a later filter (lane SB's subject) can grow the record or add an overload without a second meaning; both answer `assistant-agent-forbidden` without the access policy, as every service call does; for Lars to review. |
| CH4 | Built by lane CH (2026-10-28): **the service uploads to chats only: an agent's thread answers `assistant-conversation-not-found`, while the endpoint keeps uploads to a thread for a participant of its subject (`assistant-subject-access-denied` below)**, because `INhAssistantConversations` serves chats only everywhere else; for Lars to review. |
| CH5 | Built by lane CH (2026-10-28): **an agent offers uploads when its profile, or a fallback, permits the higher of the agent's own classification and `DefaultClassification` together with the execution region (`NhAssistantRegionChecks.PermitsTogether` on the registry), and a profile that is not registered offers nothing; a skill's classification or profile is not considered**, because that is the classification a person's message with an upload carries when its turn resolves the profile, classifications are a set rather than an order, and a message starts without a skill; the static check does not ask the resolver about availability; for Lars to review. |
| CH6 | Built by lane CH (2026-10-28): **the same rule also refuses the upload itself with `409 assistant-files-not-accepted`, in the endpoint and the service, not only `uploads: null` in `GET agents` and `GET status`**, because one rule (`RulesOf`) decides both and a client that ignores `uploads` would otherwise store a file that every turn refuses; `assistant-files-not-accepted` rather than `assistant-classification-not-permitted`, because it says what `uploads: null` says; for Lars to review. |
| CH7 | Built by lane CH (2026-10-28): **`INhAssistantFileRetention.ForgetConversationAsync` returns `TaskResult` and runs with the caller's cancellation, after the archive's live notifications; a failed result or any exception but cancellation and out-of-memory is logged as a warning with the conversation id, the reason and the code or exception type, never its messages; registered with `UseFileRetention<T>()` (scoped, a later call replaces it) and optional**, because the archive has committed and must stand, the application's messages may name files, and a cancelled request is not a failure of the application; a participant who leaves and a refused (busy) archive forget nothing; for Lars to review. |
| CH8 | Built by lane CH (2026-10-28): **`NhAssistantForgottenConversation(ConversationId, AgentId, Reason)` with `TenantId` and `Subject`, and the reasons `archived` and `subject-forgotten`; no list of files**, because the assistant keeps no list of uploads (only messages name them) and the file source stored them by conversation (`NhAssistantFileUpload.ConversationId`); the user retention participant does not call it, since it keeps the person's conversations; for Lars to review. |
| CH9 | Built by lane CH (2026-10-28): **sample cases SPM-358 and SPM-359: the messaging channel gains `POST assistant-channel/chat-list` (each chat with a link token of its own that expires with the presented link), `agents` and `files` (`multipart/form-data` with the token as a form field) and `files` on messages; `ProjectMediaUploadRetention` deletes the content and then the upload folder through the media library's storage modules; the quick answers agent runs on a new profile `project-assistant-internal` (internal only, no fallback)**, because a listed chat needs its own credential to continue in it, the channel cannot pass the media library's request-based authorization, and the existing agents all have a confidential fallback; the tests run in `AssistantChannelFileSamplesTests` on a host derived from the file host (`AssistantFileSampleHost` is no longer sealed); for Lars to review. |
| CH10 | Built by lane CH (2026-10-28): **two release sections for the `NewHeap.Platform.AI.Chat` unit, each with one breaking row (the new interface members; uploads not offered or refused where the profile chain may not receive them), no new error code, no Angular change**, because the composer already hides its attach control for `uploads: null`; for Lars to review. |
| CH11 | Built by lane CH (2026-10-28): **library tests in `AssistantConversationChannelTests`: theories on SQL Server and PostgreSQL for the lists, uploads, refusals, thread uploads, archive and subject retention, and one PostgreSQL fact for the profile-chain check over `GET agents` and `GET status`**, because the check reads no data of its own; two existing assertions changed with the visible behavior: the project assistant of `AssistantUploadEndpointTests`, on an internal-only profile, now offers no uploads, and `AssistantFileTests` reads the rules of the agent in effect; for Lars to review. |

### Lane RQ

| # | Choice |
|---|---|
| RQ1 | Built by lane RQ (2026-10-28): **the run keeps the asking chat and turn only when the consultation answers requests there and the call runs in a person's own chat turn (no run, no consultation's turn, no shadow, budget purpose `chat`, no agent invocation scope)**, so a request of an agent run, an MCP client or a proposal execution keeps none, because an answer posted into another agent's thread about another subject would widen who reads it, and nothing is stored that nothing uses; the post checks again that the conversation is a chat; for Lars to review. |
| RQ2 | Built by lane RQ (2026-10-28): **as data the ceiling decides the answer route (`NhAssistantConsultationCeilingBuilder.AnswerRequests`) for every consultation under it that takes requests, without a column of its own; an agent's ceiling answers in the asking chat only when the application's does too, and the route needs `AllowRequests()`**, because the brief puts columns only on the run and on the requests-only flag, and where model output lands is the developer's decision, as `AllowMcp` is; for Lars to review. |
| RQ3 | Built by lane RQ (2026-10-28): **the answer is the run's result as people read it (in the person's culture from the application's directory, else the run's), the summary when the run has no result, cut at `MaxMessageChars` with a note that the thread holds all of it; the title is "Answer to your request about {subject}" in English or Dutch, the link the subject type's `OpenAt` (none without one) with the chat's own label, untrusted and without `Notify`; once by the end's compare-and-set and the idempotency key `request-answer:{runId}`**, because the chat already renders application messages safely, there is no route to a thread the chat could open, and `RequestAnswered` keeps notifying the person; for Lars to review. |
| RQ4 | Built by lane RQ (2026-10-28): **"restricted results" are both a run with `UsedRestrictedResults` (checked when it ends) and a consultation classified `Restricted` (no asking chat is kept when the request is made); a shadow run keeps none either**, because what the agent read with its own grant never leaves its thread, and a restricted answer, which the asking conversation does not preview, would show in full as a message; for Lars to review. |
| RQ5 | Built by lane RQ (2026-10-28): **the post is `ReadableBy` the initiator in the tenant of the asking chat; a person who can no longer open it gets nothing, logged with the run id and a reason only, and the run's end stays as it was; in a shared chat its participants read the answer too, which the guidance says**, because that is how the asking turn's tool results reach them as well; for Lars to review. |
| RQ6 | Built by lane RQ (2026-10-28): **a request that waits for a paused agent as an input, and is claimed by a resume run later, answers in the thread only**, because the brief keeps the asking chat on the run, not on the input; for Lars to review. |
| RQ7 | Built by lane RQ (2026-10-28): **with `RequestsOnly()` the settings of a consultation's own turn are `MinimumRole`, `Proposals`, `Limits`, `Tools`, `History` and `Instructions`, refused after the registration's configure with the names in the message, as is `AllowRequests(false)` and `AnswerRequests` without requests; `Description`, `Policy`, `Exposure`, `DataClassification` and `AnswerRequests` stay; `RequestsOnly()` clears the default proposal route, and only then does the request tool's description start with the consultation's description**, so the request tool of a consultation that is also consulted keeps its listing; for Lars to review. |
| RQ8 | Built by lane RQ (2026-10-28): **as data `requestsOnly` needs `allowRequests` (`allowRequests: required`) and refuses `instructions`, `minimumRole`, `routes`, `proposalRole`, `proposalPolicy`, `toolSelectors` and the limits and history (`limits`) with `not-allowed`; a stored row that breaks this is out of effect as invalid; an agent package writes `requestsOnly: true` without those fields and only then, the package fields name `requestsOnly` only when it is set, and it is lockable**, so packages and links of every other consultation keep their hashes and a host before this version refuses such a package instead of importing a consultation that answers too; for Lars to review. |
| RQ9 | Built by lane RQ (2026-10-28): **the administration reports booleans (`requestsOnly`, `answerRequestsInAskingConversation`) on the code, data and ceiling DTOs, an empty `consultToolId` for a requests-only consultation and its askers by the request tool; the Angular models and the mock API gain these as optional fields, without a form**, because no Angular screen edits these settings yet, so no texts are needed; for Lars to review. |
| RQ10 | Built by lane RQ (2026-10-28): **one migration `RequestAnswersAndRequestsOnly` on both providers with two nullable columns on `AssistantRun` and a `bit`/`boolean` column defaulting to false on `AssistantAgentConsultationDefinition`, without an index or foreign key on the asking conversation**, because nothing queries runs by it and `AssistantConsultation.AskingConversationId` has none either; for Lars to review. |
| RQ11 | Built by lane RQ (2026-10-28): **SPM-360 extends the intake reviewer's registration of SPM-278 with `AnswerRequests` and a new test; SPM-361 makes the planner take requests only in code and gives the project follow-up a requests-only consultation about the whole application as data (`SampleAgentConfigurationService.FollowUpRequestsOnly`, which the test saves and removes); SPM-362 is executable: `projects.next-milestone` in Core with `IProjectPlanningMilestones`, implemented in the API with the official MCP client and `SampleProjectManagement:PlanningService` (`Url`, `ApiKeyCredential`, no development default), registered in `Program.cs`, tested without the API host against the sample's MCP test server, with the importer refusing the MCP exposure**, because the library surface allows the wrapper; for Lars to review. |

### Lane AW

| # | Choice |
|---|---|
| AW1 | Built by lane AW (2026-10-29): **one internal static gate, `NhAssistantAgentWorkGate.RefusalOf(options)`, answers `assistant-disabled` while the assistant is disabled and `assistant-agent-work-off` (409) while `AgentWork` is off, read from `IOptionsMonitor<NhAssistantOptions>` on every call; it replaces the `Enabled` checks of triggers (publish, start, every fire, the agent refusal that schedules, webhooks, hand-offs and replays share), runs, executions, the execution queue, `canStart`, consultations and application messages, while chat turns, `INhAssistantConversations` (lane CH's lists and uploads included), the endpoints' filter and the hub keep checking `Enabled` only**, because a function over the options needs no registration and no constructor change except the consultation service's, and a person's turn in a chat or in an agent's thread is no agent work; for Lars to review. |
| AW2 | Built by lane AW (2026-10-29): **a refusal of the whole host (`IsHostRefusal`: disabled or agent work off) is not audited per event or start (`PublishAsync`, `StartAsync`, the early refusal of webhooks), while fires through `FireAsync` (schedules, hand-offs, follow-ups, resumes) record their rejection as with the assistant disabled; with agent work off an event still queues the revalidation of the open proposals it concerns, before its triggers are refused**, because the audit would otherwise record every event the application publishes while agent work is off, and people keep deciding open proposals, which must not be stale; for Lars to review. |
| AW3 | Built by lane AW (2026-10-29): **a run queued before the switch ends cancelled with `assistant-agent-work-off` (class `policy`) without claiming its inputs, as with the assistant disabled; a run that is working finishes its turn and takes no further turn for messages that arrived meanwhile; a queued run that lost its operation is recovered only while agents work, also while the assistant is disabled, where it used to be queued again and then cancelled with `assistant-disabled`**, because the switch should not cut a model call in half, and an orphaned run that stays queued is queued once agents work again instead of being lost; for Lars to review. |
| AW4 | Built by lane AW (2026-10-29): **"application messages" are `INhAssistantConversationMessages.PostAsync`: refused with `assistant-agent-work-off`, and with it lane RQ's answer of a request run in the asking chat (skipped and logged with the code; the run's end stays as it was); the assistant's own report of an execution the application made (`execution-report-`, `execution-unknown-`) posts through an internal overload of `NhAssistantConversationMessages` without the gate, which the core package now also registers as its class**, because that report tells people about work the application already did, also of an inline approval in a person's chat, which keeps working; for Lars to review. |
| AW5 | Built by lane AW (2026-10-29): **approved proposals of agent threads are held as for a stopped agent: a decision records the approval and queues no execution, an execution that runs anyway puts the proposal back to `approved` without its operation, and the maintenance job queues missing executions only while agents work, so its first pass after the switch releases them; reminders of pending proposals keep going while the assistant is enabled; an inline approval in a person's chat executes in the turn**, because people keep deciding while agents do not work, and the maintenance pass already releases the proposals a stop held; for Lars to review. |
| AW6 | Built by lane AW (2026-10-29): **resuming a paused agent, or a paused skill of an active agent, is refused with `assistant-agent-work-off` (409) while agent work is off; pausing, stopping, switching off, resuming a stopped or switched-off agent and the kill switch serve as usual; with the assistant disabled the resume behaves as before**, because a resume starts one run per thread for the events that waited, which the gate would refuse, and nothing would start them later; for Lars to review. |
| AW7 | Built by lane AW (2026-10-29): **the reports are `NhAssistantStatusDto.AgentWork` (`agentWork`, an init property, false while the assistant is disabled), `NhAssistantAgentState.AgentWork` (default true) with `Active` false while it is off, and `NhAssistantAdminAgentDto.AgentWork` on every agent; no new endpoint and no breaking row**, because `GET admin/agents` is an array that cannot carry a field of its own, the agent list reads only agents, and every addition is optional for clients; for Lars to review. |
| AW8 | Built by lane AW (2026-10-29): **the agent list shows a warning banner (`role="status"`, `data-agent-work-off`) above the search when any listed agent says `agentWork: false`, with a title and one text in English and Dutch; the statuses, filters and the agent page stay as they are; `assistant-agent-work-off` gets texts as an error and as a tool call's result; the mock API takes `agentWork` in its scenario and `setAgentWork`, and refuses the resumes of AW6 with 409**, because the banner explains every agent at once, and the status of an agent is its own state, which comes back when agent work is on; for Lars to review. |
| AW9 | Built by lane AW (2026-10-29): **a consultation is refused first, before the asking turn's consultation limit counts it, recorded as any refused consultation, and the request tool's fire is refused by the trigger gate; `NhAssistantConsultationService` takes `IOptionsMonitor<NhAssistantOptions>` as its last constructor parameter**, because a refusal of the host should not use up the turn's consultations; for Lars to review. |
| AW10 | Built by lane AW (2026-10-29): **SPM-365 is a new case: the development settings name `AgentWork` with its default, the maintenance page's descriptions say what it reports, and the evidence runs the agent run host with `AgentWork` false (status, administration, maintenance page, a refused intake review, the owner's chat with the refused consultation of the intake reviewer) and switches it on by reloading the configuration; no sample front-end change, as the administration is the library's**, because the sample shows the setting as an operator uses it and the reload as hosts apply it; for Lars to review. |
| AW11 | Built by lane AW (2026-10-29): **library tests in `AssistantAgentWorkSwitchTests`: four theories on SQL Server and PostgreSQL (triggers, schedules, webhooks, starts, follow-ups and a queued run; held and released proposals; hand-offs, consultations, application messages and a person's chat; the state and resumes) and one PostgreSQL fact over HTTP (status, administration, a refused resume, a person's chat and inline decision); the pinned JSON names of `GET status`, the admin agent and the agent state gain `agentWork`**, because the gate reads no data of its own while the work it refuses does; for Lars to review. |
