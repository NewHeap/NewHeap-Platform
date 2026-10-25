# Platform Feedback on 20.0.0 Plan

A consuming application reported library findings. Most were made against NuGet 20.0.0 and
`@newheap/platform-ai-chat` 0.19.0; the older ones against earlier releases are still open on 20.0.0.
This plan records one choice per finding. It is a plan, not usage documentation: the guidance rules,
the release notes and the sample cases are authoritative once it is built.

## Status

Decided by Lars on 2026-10-25: LS1 to LS46, every one as advised (option (a)). The decisions are summarized
below; the sections under "Choices" keep the options that were weighed. Not built yet; delivery is trains 21 to 23
(LS46).

Already settled on 20.0.0, so not part of this plan:
- The AI startup validator runs in its own scope (7.14.2).
- Generated tools return readable `{ success, code, message }` failures (10.0.0).
- SQL Server principal verification reports `verificationCheck` (7.1.0). Its function policy is LS35.

## Decisions

| # | Decision |
|---|---|
| LS1 | **Generated regexes without a match timeout** for the four linear fixed patterns (tool selector, file extension, media type, artifact type); patterns over configured wildcards and user patterns keep their timeouts. |
| LS2 | **PostgreSQL binds omitted schema-search strings as `NpgsqlDbType.Text`**, with provider and CLI tests for omitted schema, omitted term, both, explicit schema and search-and-describe. |
| LS3 | **Failure codes and message keys are checked against their column limits before writing**: a key that is too long falls back to the generic failure key, a code to `handler-rejected`, the full text goes to the log; proved on both providers for failed and retry results. |
| LS4 | **The step's original error wins when recording the failed step fails**; the secondary failure is logged; a lost ownership or contention signal still wins. |
| LS5 | **`TimedOut` only when the timeout fired; otherwise the fenced durable cancellation is checked and reported as `Cancelled`**, with a PostgreSQL regression and a sample case. |
| LS6 | **Every early refusal of the tool invoker audits the code it returns**, and an invalid evidence shape writes an audit too; the specialized fields stay. |
| LS7 | **A start check and a save check that every profile in use permits the execution region**, and the non-retried run-time code `assistant-model-region-denied`. |
| LS8 | **A warning when a presentation is dropped** (presenter, tool, broken rule, no content), every cap and `SupportedCultures` documented, the field cap kept at eight. |
| LS9 | **Shadow proposals follow the live duplicate rules as if open** within their proposal lifetime, reported as `AlreadyOpen`, so the evaluation compares what live would propose. |
| LS10 | **`AddAssistantAgentRuns` chooses a queue and concurrency, and background operations get a reserved share per queue within the global limit** (`WithReservedQueueConcurrency`). |
| LS11 | **`NhAssistantOptions.RunSchedules`, true by default**: false registers no schedule service, no data schedule sync and no recurring jobs, and removes none. |
| LS12 | **An additive `AgentRunId`** on `NhAiInvocationContext` and `NhAiAgentInvocationScope`; `RunId` and `CorrelationId` keep their meaning. |
| LS13 | **Receipts and deferred fires carry the queued input's id, and `INhAssistantAgentRunLookup.FindByEventAsync`** finds the run or the waiting input by event key, with an index migration. |
| LS14 | **A status filter on the inbox** (`pending`, `awaiting-application`, `execution-unknown`) **and `INhAssistantProposalCounts`**, with the same visibility; additive DTOs. |
| LS15 | **`UseSystemSubject(s => s.Members<T>())`**: an `INhAssistantSystemMembers` lists the people of the whole application and per division; roles still come from the policies; the notifier uses them next to the participants. |
| LS16 | **Decision facts gain `SubjectChanged`** from the execution's precondition, **and `DecideAsync` refuses a changed subject** with `assistant-proposal-stale` when the proposal requires an unchanged subject. |
| LS17 | **`ExecuteInApplication(..., o => o.AlsoInline())`** hands an inline approval to the application too and posts its report into that conversation; AE3 stays the default. |
| LS18 | **`KeepTurnsAfterDisconnect`, off by default**: the turn continues up to its timeout with the principal captured at the start; writes to the closed stream are dropped; the client reloads the snapshot. |
| LS19 | **`INhAssistantConversationTurns`** (`SendAsync`, `DecideAsync`) for a person the application authenticated, in an invocation scope, able to outlive the caller. |
| LS20 | **`run.SkillsInChat()`** offers `use_skill` in a person's conversations with the agent, without triage, recorded per turn. |
| LS21 | **`run.AllowQuestions()`** offers `ask_question`: a question with choices and an addressee, answered only by the addressee; the turn waits as for an approval; Angular shows it. |
| LS22 | **`MaxConsultationsPerTurn`, unlimited by default**, globally and per agent, with `assistant-consult-turn-limit`. |
| LS23 | **`INhAssistantAgentDefinitions`** (`UpdateAsync`, `ResetAsync`, `DeleteAsync`) with the administration's checks, locks and audit; MCP assignments kept unless named. |
| LS24 | **`INhAssistantAgentDirectory`** returns agents as in effect: source, name, description, instructions or asset with problem, selectors, autonomy, profile, run state, shadow, metadata. |
| LS25 | **`IndexByEach`** stores up to 50 values per entry in a child table (migration); `ListAgentsAsync` matches any. |
| LS26 | **One display-name helper everywhere**: the editor title, the import dialog and the copies use `agentDisplayName`. |
| LS27 | **The hub path documented next to the route prefix, and the query token read for the configured hub paths**; the default stays. |
| LS28 | **`INhAssistantMcpServers`**: list, create, update, delete, test, sync, tool update, assign and unassign agents, with secrets and audit as in the administration and a new audit kind for assignments. |
| LS29 | **`NhAssistantMcpOptions.AllowLoopback`, off by default**, refused at startup in Production; metadata hosts stay blocked. |
| LS30 | **`INhAssistantInstructionAssetUsage.ListAsync(assetId)`**; a new asset version still never takes effect by itself. |
| LS31 | **Promote a data shadow variant onto its live agent** as an explicit administrator action through the update path, then remove the variant; HD13 and HD10 hold. |
| LS32 | **`GetPackageAsync(packageId)`** on `INhAssistantAgentPackages`, with a release-note row. |
| LS33 | **`ReadAsync(agentIds)`** returns parts and hashes as an export builds them, without the export audit. |
| LS34 | **`tls-certificate-untrusted`**, not transient, with a trust hint, on both providers; provider code, stage and redaction stay. |
| LS35 | **The strict function policy stays and is documented**, with the missing release note for `verificationCheck`. |
| LS36 | **No MySQL or MariaDB provider now.** |
| LS37 | **The deterministic chat client can validate structured-output schemas against strict provider rules**, with guidance; normalization stays with the application. |
| LS38 | **`NhTransactionFactoryAsync` next to the synchronous factory**; CAP registers one with `BeginTransactionAsync`; tests on both providers. |
| LS39 | **A governed deadline is a failed result `ai-model-timeout`** with outcome `TimedOut`, for chat and streaming; a caller's cancellation still throws; release-note row. |
| LS40 | **Two slots on `NhAiAgentCreateRequest`**: a chat history provider factory and an AI context provider factory. |
| LS41 | **`NhModalComponent` owns accessibility**: `aria-modal`, `aria-labelledby`, a translated close label, focus handling and Escape when closeable. |
| LS42 | **A public document component in nh-ai-chat** for untrusted Markdown with relative links and an optional diagram renderer the application loads. |
| LS43 | **A public-website capability in the foundation skill, and Angular pins from platform-common's peer range.** |
| LS44 | **The LS6 release note supersedes the v20.0.0 note on refusal codes.** |
| LS45 | **Nothing waits for the application's evidence.** |
| LS46 | **Three trains**: 21 robustness and small fixes (LS1 to LS9, LS12, LS16, LS26, LS27, LS34, LS35, LS44); 22 runs, decisions, agents and MCP (LS10, LS11, LS13 to LS15, LS17, LS22 to LS25, LS28 to LS33); 23 chat, AI and front end (LS18 to LS21, LS37 to LS43). |

## Rules for all of it

- Every change is opt-in or compatible unless its choice says otherwise. A breaking change gets a
  `Breaking change | Required action` row.
- Every change follows the repository rules:
  - code and migrations for SQL Server and PostgreSQL where persistence changes;
  - tests with authorization negatives;
  - a release-notes section per release unit;
  - guidance and consumer guide updates;
  - an executable sample case;
  - English and Dutch texts for Angular parts.
- Nothing names the consuming application.

## Choices

Each choice lists the options that were weighed. "(advice)" marks the recommendation, which Lars followed for all
of them.

### A. Robustness

#### LS1. Regex match timeouts on fixed patterns

Four fixed, linear patterns use a 100 ms match timeout. A busy machine exceeds it, so a host build fails
with `RegexMatchTimeoutException`. The four are the tool selector (`NhAssistantAgentDefinition`), the file
extension (`NhAssistantFiles`), the media type (`NhAiFiles`) and the artifact type (`NhAssistantArtifacts`).

- **(a) Generated regexes without a timeout (advice).** All four patterns are linear, so they cannot run
  away. A start-up check then never depends on CPU time. Regexes over configured wildcards (the MVC
  bridge) and user patterns (the proxy) keep their timeouts.
- **(b) `RegexOptions.NonBacktracking` with the timeout removed.** Also safe, but slower to construct,
  and it gains nothing for these patterns.
- **(c) A hand-written parser per pattern.** No regex at all, at the cost of more code to test.

#### LS2. PostgreSQL schema search with omitted parameters

`SearchSchemaAsync` binds an omitted `schemaName` or `searchTerm` as an untyped `DBNull`. PostgreSQL 17
then fails with SQLSTATE 42P08. No PostgreSQL test omits either value.

- **(a) Bind nullable strings as `NpgsqlDbType.Text` (advice).** Add provider and CLI tests for: schema
  omitted, term omitted, both omitted, explicit schema, and search-and-describe. Permission verification,
  visibility and limits stay as they are.
- **(b) Explicit `::text` casts in the SQL.** Equivalent, but easy to forget in the next query.

#### LS3. Failure codes and message keys that do not fit their columns

`GetFailure` stores a failed result's message format as `FailureMessageKey`, which allows 300
characters. A longer message fails completion with PostgreSQL 22001. The failure code allows 100
characters, but its fallback only applies above 200.

- **(a) Validate against the column limits before writing (advice).**
  - A key that is too long falls back to the existing generic failure key.
  - A code that is too long falls back to `handler-rejected`.
  - The full text goes to the operational log only.
  - Expected failures stay terminal, and retries stay intact.
  - Prove on both providers that oversized failed and retry results complete or reschedule.
- **(b) Truncate.** Not advised: a truncated key matches no translation.

#### LS4. The original error lost when recording a failed step fails

`NhBackgroundOperationContext` awaits `CompleteStepAsync` inside its exception handlers. A database
failure there replaces the step's own error, including a cancellation.

- **(a) The original error wins; the secondary failure is logged (advice).** If recording the step
  throws, log that failure with the operation id and rethrow the original exception. The one exception
  is a lost ownership (fence) or contention signal: the attempt is no longer ours, so that signal wins,
  as it does today. Tests cover a cancellation, a handler error and a database failure while recording.
- **(b) An aggregate exception carrying both.** More information, but every catch that expects the
  original type breaks.

#### LS5. A durable cancellation classified as a timeout

The runner classifies an `OperationCanceledException` as `TimedOut` unless the heartbeat already
recorded a stop. A handler that observes the durable cancellation first and stops promptly is therefore
reported as timed out.

- **(a) Classify by what actually fired (advice).**
  - `TimedOut` only when the timeout source fired.
  - Otherwise, check the fenced durable cancellation that already exists (`IsCancellationRequestedAsync`
    for this attempt), and if it was requested report `Cancelled`.
  - Otherwise keep today's handling.
  - A PostgreSQL regression and a sample case request cancellation between heartbeats.
- **(b) Only the timeout-source check.** This misses a durable cancellation that the heartbeat has not
  seen yet.

#### LS6. Audit result codes for every early refusal

TA2 made four refusals audit their own code: the gate, the capability, the effect and approval
delegation. These exits still write `ResultCode = null`:
- input too large;
- approval required;
- approval invalid or rejected;
- budget invalid or denied;
- concurrency;
- deadline expired;
- idempotency key invalid or duplicate;
- result too large;
- verification failed;
- exception.

An invalid evidence shape writes no audit at all.

- **(a) Every exit audits the code it returns (advice).** The invalid evidence shape also writes an
  audit. The specialized approval and idempotency fields stay. The audit stays content-free. Tests
  compare the returned code with the audited code for every exit.
- **(b) Only approval and idempotency**, the two the report named.

#### LS7. An execution region that no profile permits

The assistant runs in `local` unless `UseExecutionRegion` is set. A profile that permits only `eu`
refuses every model turn at run time. The failure is `assistant-model-unavailable`, which is classed
transient, so the run is retried, and no start check catches it.

- **(a) A start check, a save check, and a code that is not retried (advice).**
  - At startup, every profile the assistant uses must permit its region, through its fallbacks: the
    agents', the skills', the triage profile and the chat profile. Otherwise startup fails, naming the
    profile.
  - A data skill or agent created in the administration whose profile does not permit the region is
    refused at save.
  - At run time the turn fails with the new, non-retried `assistant-model-region-denied`.
- **(b) A logged warning only.**

#### LS8. Proposal presentations dropped without a word

A presentation with more than eight fields, or one that is otherwise invalid, is dropped without a log,
and the approver sees the generic summary instead. Cultures come from `SupportedCultures`, which is
documented nowhere.

- **(a) A warning, the caps documented, the cap kept (advice).**
  - Log a warning when a presentation is dropped, naming the presenter, the tool and the rule it broke,
    without content.
  - Document every cap and the `SupportedCultures` setting.
  - Eight fields stays the limit, so approvers read a short card. The presenter is the application's to
    fix.
- **(b) Raise the cap to sixteen fields, plus the warning and the documentation.**
- **(c) Keep the first eight fields.** Not advised: an approver would decide without fields they never
  saw.

#### LS9. Shadow proposals repeat unchanged findings

A shadow proposal never waits, so the duplicate check never holds it, and every shadow run proposes an
unchanged finding again.

- **(a) Shadow proposals follow the live duplicate rules, as if they were open (advice).**
  - A shadow proposal with the same identity (tool plus canonical arguments, in the same scope and agent)
    as one recorded within its proposal lifetime is not recorded again. It is reported with the outcome
    that a live proposal would get (`AlreadyOpen`).
  - The evaluation then compares what the live agent would really have proposed.
- **(b) Record the duplicates but mark them**, so the evaluation can filter them out.
- **(c) Leave it to the application**, which already keeps fingerprints in thread memory.

### B. Runs and hosts

#### LS10. Reserved capacity for agent runs

`AddAssistantAgentRuns` sets no queue and no concurrency. Agent runs share the default queue under the
one global lease, and a named queue would not escape that lease anyway.

- **(a) A queue and a reserved share (advice).**
  - `AddAssistantAgentRuns(o => o.Queue(...).MaxConcurrency(...))` chooses the agent runs' queue and
    their concurrency.
  - Background operations gain a reserved share per queue within the global limit
    (`WithReservedQueueConcurrency(queue, slots)`), so other work cannot starve agent runs, and agent
    runs cannot starve other work.
- **(b) Only the queue and concurrency options**, without a reservation.

#### LS11. A host that knows schedules but does not run them

Two hosts register the same agents, but only one runs Hangfire. The other host must remove NewHeap's
schedule registrations by their internal type names. Worse, each host's data schedule sync removes jobs
that are missing from its own snapshot.

- **(a) `NhAssistantOptions.RunSchedules`, true by default (advice).**
  - When false, the host registers no schedule hosted service, no data schedule sync and no recurring
    jobs, and it never removes jobs.
  - Triggers, runs and the administration work as usual.
  - Document which host must keep it true.
- **(b) Detect a missing Hangfire server and skip.** Not advised: a host with storage but no server is
  exactly the case that cannot be told apart.

#### LS12. The agent run id on the invocation scope

A tool sees the agent run's id only as the invocation's `CorrelationId`; `RunId` is the turn id.

- **(a) An additive `AgentRunId` (advice)** on `NhAiInvocationContext` and `NhAiAgentInvocationScope`.
  `RunId` and `CorrelationId` keep their meaning, because proposal hashes and usage records depend on
  them.
- **(b) Document `CorrelationId` as the run id.** It already is one, but the name invites mistakes.

#### LS13. Finding the run a trigger event started

Receipts carry no event key and no input id. A deferred fire returns no ids at all. No public query
finds a run by its event key.

- **(a) Ids on the receipts and a public lookup (advice).**
  - Receipts and deferred fires carry the queued input's id.
  - `INhAssistantAgentRunLookup.FindByEventAsync(agentId, triggerId, eventKey)` returns the run that
    consumed it, or the input while it waits, through `AssistantQueuedInput` (`EventKey`,
    `ConsumedByRunId`).
  - An index migration on the event key.
- **(b) Only the ids on the receipts**, leaving the lookup to the application.

### C. Decisions, inbox and notifications

#### LS14. Proposals on their way

The inbox and the subject work list show only pending proposals. No service counts proposals.

- **(a) A status filter and a count service (advice).**
  - `GET approvals/inbox?status=` accepts `pending` (the default), `awaiting-application` and
    `execution-unknown`, with the same visibility rules.
  - `INhAssistantProposalCounts.CountAsync(person, statuses)` returns counts per status, for digests.
  - The DTO changes are additive.
- **(b) Only the status filter.**

#### LS15. Members of the system subject

The whole application has no members, so a company-wide proposal in a thread that nobody opened
notifies nobody.

- **(a) A members provider for the system subject (advice).** `UseSystemSubject(s => s.Members<T>())`
  registers an `INhAssistantSystemMembers` that lists the people (actor ids) for `nh-system/global` and
  per division. Roles still come from the read and approve policies. The notifier uses these people next
  to the thread's participants.
- **(b) Document a custom notification policy as the way out.**

#### LS16. Staleness in the decision facts

The facts carry no subject version. A reply after the object changed is approved, and only the check
before execution closes the proposal as stale.

- **(a) Facts show it, and a decision refuses it (advice).**
  - The facts gain `SubjectChanged` (`true`, `false` or unknown), from the same precondition the
    execution runs (`DescribeAsync` plus the version compare, or the application's own precondition).
  - `DecideAsync` refuses a changed subject with `assistant-proposal-stale` when the proposal requires an
    unchanged subject, closing it as the execution would.
- **(b) Only the flag in the facts.**

#### LS17. Inline approvals of tools the application executes

A requester who approves in their own chat turn runs the tool's function (AE3). No hand-off to the
application happens, and no report reaches the conversation.

- **(a) An opt-in per tool (advice).**
  - `ExecuteInApplication(toolId, reportWithin, o => o.AlsoInline())` hands an inline approval to the
    application too.
  - The turn continues with "handed to the application", and the report is posted into that
    conversation.
  - AE3 stays the default.
- **(b) Always hand off inline approvals of a declared tool.** Breaking: it changes AE3 for every host.
- **(c) Keep AE3.**

### D. Chat

#### LS18. Turns that outlive the client

A chat turn passes `RequestAborted`, so closing the browser cancels it.

- **(a) An option to keep a turn running after the client left (advice).**
  - `KeepTurnsAfterDisconnect`, off by default.
  - The turn continues up to its timeout with the principal captured at the start, without the request,
    and its writes to the closed stream are dropped.
  - The client reloads the snapshot; there is no resumable stream.
- **(b) Chat turns as background operations with a resumable stream.** Complete, but a large change.
- **(c) Keep it as it is.**

#### LS19. A server-side service for a person's turn

A person's turn starts, and their chat approvals are decided, only through HTTP endpoints.

- **(a) `INhAssistantConversationTurns` (advice).**
  - `SendAsync(person, conversationId, message, options)` and `DecideAsync(person, conversationId,
    approvalId, decision)`, for a person the application authenticated
    (`NhAssistantDecidingPerson`).
  - The turn runs in an invocation scope with that principal, so tools authorize without a request.
  - It returns the turn's result, and it can outlive the caller (LS18).
- **(b) Only decisions; turns stay HTTP-only.**

#### LS20. Skills in a person's chat

Skills and `use_skill` work only in agent threads.

- **(a) An opt-in per agent (advice).**
  - `run.SkillsInChat()` offers `use_skill` in a person's conversations with the agent. There is no
    triage; the model chooses.
  - The chosen skill is recorded per turn.
- **(b) Not now.**

#### LS21. Structured questions

Clarifications are text with numbered options, and in a shared conversation anyone answers.

- **(a) An opt-in question tool (advice).**
  - `run.AllowQuestions()` offers a governed `ask_question` that posts a question with choices and an
    addressee (by default the person whose turn it is).
  - Only the addressee answers, through an endpoint like an approval, and the turn waits as for an
    approval.
  - Angular shows the choices.
- **(b) Not now.**

#### LS22. A consultation limit per turn

No consultation limit exists per turn. Each consultation counts toward the turn's tool-call limit.

- **(a) `MaxConsultationsPerTurn`, unlimited by default (advice).** Set globally in `NhAssistantLimits`
  and per agent. Checked before a consultation costs anything, with the new code
  `assistant-consult-turn-limit`.
- **(b) A default of three.** Breaking for hosts that consult more.

### E. Agents and metadata

#### LS23. Changing agent definitions from code

Code can create an agent, but only the administration's HTTP API can update, reset or delete one.

- **(a) A separate public service (advice).**
  - `INhAssistantAgentDefinitions` with `UpdateAsync(id, request, expectedVersion)`, `ResetAsync` and
    `DeleteAsync`, for agents created at runtime and for overrides of agents in code.
  - It uses the same checks, package locks and audit as the administration.
  - MCP assignments are kept unless named.
  - A separate interface does not break implementations of `INhAssistantAgentConfiguration` (as HD8
    did).
- **(b) New members on `INhAssistantAgentConfiguration`.** Breaking.

#### LS24. Agents with their definition in effect

No public read returns an agent's description, instructions, enabled state and run state together.

- **(a) `INhAssistantAgentDirectory` (advice).** `GetAsync` and `ListAsync` return each agent as it is
  in effect:
  - source and display name (a key for agents in code);
  - description and instructions (or the asset reference with its problem);
  - tool selectors, autonomy and profile;
  - run state, shadow and metadata.
- **(b) Extend `INhAssistantAgentMetadata`'s result.** Mixes metadata with the definition.

#### LS25. A searchable value per list item

`IndexBy` stores one value per entry, so an agent that works for several departments cannot be found
per department.

- **(a) `IndexByEach` (advice).** `IndexByEach(value => items)` stores up to 50 values per entry in a
  child table (migration). `ListAgentsAsync(name, value)` matches any of them. Existing entries keep
  `IndexBy`.
- **(b) Not now.**

#### LS26. The editor title of an agent in code

The editor title shows a code agent's name key untranslated. The import dialog falls back to an
untranslated name in one place, and five components copy the name logic.

- **(a) One helper everywhere (advice).** The title and the import dialog use `agentDisplayName`, and
  the copies use it too. A spec covers the title.
- **(b) Fix only the title.**

#### LS27. The hub path

The hub maps on the host's root (`/hub/assistant`), outside the route prefix. The access token is read
from the query string only for paths under `/hub`.

- **(a) Documented, and the token follows the configured path (advice).** Document the hub path next to
  the route prefix. Read the query token for the configured hub paths, not only `/hub`, so a hub moved
  to `/hubs/assistant` or under the prefix keeps working. The default stays.
- **(b) Map the hub below the prefix by default.** Breaking for proxies and clients.

### F. MCP servers

#### LS28. An application service for MCP servers

MCP servers, their tools and their agent assignments are managed only through admin endpoints. An
application that writes the tables bypasses audit and secrets handling.

- **(a) `INhAssistantMcpServers` (advice).**
  - List, create, update, delete, test, sync, update a tool, and assign or unassign agents.
  - Secrets and audit as in the administration, plus a new audit kind for agent assignments.
- **(b) Only assignments.**

#### LS29. Loopback MCP servers in tests

Loopback is allowed only in Development, and no option exists.

- **(a) `NhAssistantMcpOptions.AllowLoopback`, off by default (advice).** Allowed only when set, and
  refused at startup in Production. Metadata hosts stay blocked.
- **(b) A helper in the testing package only.**

### G. Skills and packages

#### LS30. Which skills and agents name a text asset

No query lists the data skills and agents that use an asset.

- **(a) A usage query (advice).** `INhAssistantInstructionAssetUsage.ListAsync(assetId)` returns the
  skills (with version and status) and the agents that name it. A new asset version still never takes
  effect by itself (IA9).
- **(b) The query, plus candidates made automatically for a new asset version.**

#### LS31. A candidate for an agent's definition

Skills and triggers have candidates; an agent's definition does not. A data shadow variant never goes
live (HD13).

- **(a) Promote a variant onto its live agent (advice).**
  - An explicit administrator action copies the variant's definition onto the live agent through the
    update path, with locks, version and audit, and then removes the variant. Its skills move along when
    asked.
  - The variant itself never becomes live, so HD13 holds.
  - HD10 stays: an agent in code that starts in shadow gets no data variants.
- **(b) Versions and candidates for agent definitions**, like skills, with a migration.
- **(c) Not now.**

#### LS32. The installed package version

After an unlink, `GetLinkAsync` returns nothing, so the installed version is unknown.

- **(a) `GetPackageAsync(packageId)` (advice).** It returns the stored package (id, version, hash, its
  agents, imported at and by) with its current links. Added to `INhAssistantAgentPackages`, which only
  NewHeap implements, with a release-note row.
- **(b) A separate interface.**

#### LS33. Reading an agent's parts without an export

Comparing an installed agent with a package means exporting it, which is audited as an export.

- **(a) `ReadAsync(agentIds)` (advice).** It returns the parts and their hashes exactly as an export
  would build them, without the export audit, on `INhAssistantAgentPackages`.
- **(b) Keep the export.**

### H. Database read

#### LS34. An untrusted TLS certificate

An untrusted certificate chain reads as `connection-failed`, with a network hint, on both providers.

- **(a) An additive classification (advice).** `tls-certificate-untrusted`, not transient, with a hint
  to trust the certificate authority. The provider code, stage and redaction stay. Tests on SQL Server
  and PostgreSQL, with canaries for raw messages.
- **(b) Not now.**

#### LS35. SQL Server function reads in principal verification

Any user function with SELECT or EXECUTE rejects the principal. Since 7.1.0, `verificationCheck` names
the check.

- **(a) Keep the strict policy and document it (advice).** Add the missing release note for
  `verificationCheck`. Allowing function reads needs security evidence about dependencies and side
  effects first.
- **(b) Allow SELECT on inline table-valued functions** without dependencies on objects that write.

#### LS36. MySQL and MariaDB

- **(a) Not now (advice).** The application's own structured reader covers its need.
- **(b) A provider for the governed profile and CLI.**

### I. AI common

#### LS37. Structured-output schemas at the provider boundary

Schemas of repeated types contain references that some providers refuse. The library neither checks
nor normalizes them, and the test fakes accept everything.

- **(a) Testing and guidance, with normalization left to the application (advice).** The deterministic
  chat client can validate structured-output schemas against strict provider rules (references only to
  top-level `$defs`, object constraints) and fail as a provider would. The guidance shows the cases. The
  report itself asks to keep provider normalization consumer-owned.
- **(b) Normalize schemas in the executor** for every provider.

#### LS38. Opening a CAP transaction asynchronously

The transaction factory is synchronous, so with CAP every asynchronous caller opens its transaction
synchronously.

- **(a) An asynchronous factory next to the synchronous one (advice).**
  - `NhTransactionFactoryAsync`, cancellation-aware, used by `StartTransactionAsync` when registered.
  - CAP registers one that uses `BeginTransactionAsync`.
  - The synchronous factory stays for compatibility.
  - Tests with CAP on both providers: cancellation while opening, outbox atomicity, rollback, nesting.
- **(b) Replace the synchronous factory.** Breaking.

#### LS39. A model timeout versus caller cancellation

A governed deadline and a caller's cancellation both end as `cancelled` and rethrow, in chat and in
streaming.

- **(a) A timeout is a failed result (advice).**
  - A governed deadline returns a failed result `ai-model-timeout`, with outcome `TimedOut` and finish
    reason `timed-out` in usage and audit, for chat and streaming.
  - A caller's cancellation still throws.
  - Budget release and audit completion are verified for both.
  - Breaking for code that caught the exception on a timeout, with a release-note row.
- **(b) A `TimeoutException` subtype**, with `TimedOut` in usage, so code that catches the exception
  keeps working.

#### LS40. Agent Framework history and compaction

The adapter builds `ChatClientAgentOptions` itself, without any history or compaction provider.

- **(a) Two slots on the create request (advice).** `NhAiAgentCreateRequest` takes an optional chat
  history provider factory and an AI context provider factory, passed to the options. The governed
  client, tool publication and invocation context stay NewHeap's.
- **(b) A callback over the whole options.** Too open: it could replace the governed client.

### J. Front end and guidance

#### LS41. Modal keyboard accessibility

`NhModalComponent` has a dialog role, but no accessible name, no close-button label, no focus handling
and no Escape.

- **(a) The modal owns it (advice).**
  - `aria-modal` and `aria-labelledby` from the title, and a translated close label.
  - Initial, trapped and restored focus, and Escape when the modal can close.
  - Stacked modals, modals that cannot close, lifecycle hooks and CSS contracts stay.
  - Proved in the management portal sample with the keyboard.
- **(b) Only the labels.**

#### LS42. A sanitized Markdown and diagram renderer

A safe Markdown renderer exists only inside nh-ai-chat, without diagrams.

- **(a) A public document component in nh-ai-chat (advice).** It takes untrusted Markdown, sibling paths
  for relative links and an optional diagram renderer that the application loads. It has the same safety
  defaults and emits link and heading events. It adds no package dependency, because nh-ai-chat already
  has the renderer.
- **(b) In platform-common.** That package would gain the Markdown dependencies.
- **(c) Not now.**

#### LS43. A public Angular website in the foundation skill

The inspector expects a front end only for the management portal, and the bootstrap pins Angular
20.3.27, while platform-common requires `^20.3.28`.

- **(a) A public-website capability and pins from the peer range (advice).**
  - The inspector and bootstrap accept an API with a public Angular front end, without login or
    database.
  - Angular pins come from platform-common's peer range.
  - Verified with restore, peer resolution, build and start.
- **(b) Only fix the pins.**

### K. Delivery

#### LS44. The v20.0.0 note on refusal codes

Not a finding of the report, but found while checking it: the v20.0.0 release note on refusal codes
lists only the four TA2 refusals. LS6 completes the rest.

- **(a) The LS6 release note supersedes it (advice).**
- **(b) Also correct the v20.0.0 note.**

#### LS45. What waits for the application's evidence

- **(a) Nothing (advice).** Every choice above has enough evidence; samples and tests prove them.
- **(b) Ask the application for a reproduction of LS2 and LS1 first.**

#### LS46. Trains

- **(a) Three trains, in this order (advice).**
  - **Train 21, robustness and small fixes:** LS1 to LS9, LS12, LS16, LS26, LS27, LS34, LS35, LS44.
  - **Train 22, runs, decisions, agents and MCP:** LS10, LS11, LS13 to LS15, LS17, LS22 to LS25, LS28 to
    LS33.
  - **Train 23, chat, AI and front end:** LS18 to LS21, LS37 to LS43.
- **(b) One train per area**, more but smaller trains.

## Lane choices

| # | Choice |
|---|---|
| LA1 | Built by lane LA (2026-10-25): **authoritative execution evidence of an invalid shape is audited as `AuthorizationDenied` with `ApprovalCode` `execution-evidence-invalid` and `ResultCode` `ai-tool-execution-evidence-invalid`, without the evidence reference or idempotency key it carried**, because the attestation as a whole is untrusted, for Lars to review. |
| LA2 | Built by lane LA (2026-10-25): **`ResultCode` is the code of the shared tool result at every exit, also for the gate refusal, the authoritative denial and the tool's own failed result**, so a failure without a code of its own audits `ai-tool-authorization-denied`, `ai-tool-execution-evidence-invalid` or `ai-tool-failed` instead of `null`, for Lars to review. |
| LA3 | Built by lane LA (2026-10-25): **the exception exit audits `ai-tool-failed`, the code the protocol adapters give the caller, and the exception still propagates**; the assistant's tool call row of an imported MCP tool keeps its own classification of the exception, for Lars to review. |
| LA4 | Built by lane LA (2026-10-25): **a missing approval keeps `ApprovalCode` `null` next to `ResultCode` `ai-tool-approval-required`**, so the assistant still turns it into a proposal, for Lars to review. |
| LA5 | Built by lane LA (2026-10-25): **the assistant follows the new codes through its existing audit relay, without an AI.Chat change**: its tool calls, `ToolInvoked` events and proposal execution results read `ai-tool-idempotency-denied`, `ai-tool-deadline-expired` and the other codes, with a breaking release-note row; the lease's own code stays in `IdempotencyCode`, for Lars to review. |
| LA6 | Built by lane LA (2026-10-25): **`AgentRunId` comes from the agent turn's run and is `null` outside a run** (a person's chat, a person's turn in the thread, a consultation, the grant check of a consultation proposal); it is not added to `NhAiInvocationContextBuilder` or the durable background-run adapter, whose `RunId` already is the operation, for Lars to review. |
| LA7 | Built by lane LA (2026-10-25): **no new sample case: SPM-308 stamps `ChangedByAgentRunId` from `AgentRunId` on the status event, and SPM-224 asserts the audited codes of the refused retry**; the real run path is proven in the AI.Chat agent run tests on both providers, for Lars to review. |
| LA8 | Built by lane LA (2026-10-25): **the LS1 test pins the infinite match timeout of the generated media type pattern by reflection and decides long valid and invalid values at the 128-character bound**, without a wall-clock assertion, for Lars to review. |
