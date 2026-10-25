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
| LD1 | Built by lane LD (2026-10-25): **the retry hint of `tls-certificate-untrusted` is `certificate-authority-trust-required`**, in the style of `network-access-required`; the message says only that the client did not trust the server's TLS certificate, for Lars to review. |
| LD2 | Built by lane LD (2026-10-25): **SQL Server maps `SEC_E_UNTRUSTED_ROOT` (`-2146893019`), `CERT_E_UNTRUSTEDROOT` (`-2146762487`) and `CERT_E_CHAINING` (`-2146762486`)**; `SEC_E_WRONG_PRINCIPAL`, `CERT_E_CN_NO_MATCH`, `SEC_E_CERT_EXPIRED`, `CERT_E_EXPIRED`, `SEC_E_ILLEGAL_MESSAGE` and `SEC_E_ALGORITHM_MISMATCH` stay `connection-failed`, so the trust hint is never wrong where SqlClient names the failed check. SqlClient 6.1.1 reports `-2146893019` on both its native and its managed network layer, for Lars to review. |
| LD3 | Built by lane LD (2026-10-25): **a certificate that .NET validation rejects, an `AuthenticationException` without an inner exception, is `tls-certificate-untrusted` on both providers**; Npgsql gives no SQLSTATE and does not say which check failed, so on PostgreSQL a host-name or expiry rejection reports the same classification, while a handshake failure with an inner platform error (no common protocol) stays `connection-failed`, for Lars to review. |
| LD4 | Built by lane LD (2026-10-25): **the real TLS case for PostgreSQL is a local endpoint that answers the SSL request and presents a self-signed certificate to Npgsql with `SslMode=VerifyFull`**, because the shared container runs without TLS and turning it on would change every PostgreSQL test of the process; SQL Server uses the shared container with `TrustServerCertificate=false`, for Lars to review. |
| LD5 | Built by lane LD (2026-10-25): **every PostgreSQL provider parameter carries its type (text, integer or oid), also the `set_config` values that are never null**, so no query can bind an untyped null; SQL Server's binding stays as it was, because SqlClient sends an untyped null as `nvarchar` and its omitted search already worked, for Lars to review. |
| LD6 | Built by lane LD (2026-10-25): **the sample MCP maps `tls-certificate-untrusted` to a trust remedy that says retrying does not help**, as SPM-237 evidence next to its tool and provider tests; no new sample case, for Lars to review. |
| LD7 | Built by lane LD (2026-10-25): **the `verificationCheck` note is a paragraph of the tool's v-next section, not a table row**, because it changes nothing for consumers; the section's one row is the TLS classification, for Lars to review. |
| LA1 | Built by lane LA (2026-10-25): **authoritative execution evidence of an invalid shape is audited as `AuthorizationDenied` with `ApprovalCode` `execution-evidence-invalid` and `ResultCode` `ai-tool-execution-evidence-invalid`, without the evidence reference or idempotency key it carried**, because the attestation as a whole is untrusted, for Lars to review. |
| LA2 | Built by lane LA (2026-10-25): **`ResultCode` is the code of the shared tool result at every exit, also for the gate refusal, the authoritative denial and the tool's own failed result**, so a failure without a code of its own audits `ai-tool-authorization-denied`, `ai-tool-execution-evidence-invalid` or `ai-tool-failed` instead of `null`, for Lars to review. |
| LA3 | Built by lane LA (2026-10-25): **the exception exit audits `ai-tool-failed`, the code the protocol adapters give the caller, and the exception still propagates**; the assistant's tool call row of an imported MCP tool keeps its own classification of the exception, for Lars to review. |
| LA4 | Built by lane LA (2026-10-25): **a missing approval keeps `ApprovalCode` `null` next to `ResultCode` `ai-tool-approval-required`**, so the assistant still turns it into a proposal, for Lars to review. |
| LA5 | Built by lane LA (2026-10-25): **the assistant follows the new codes through its existing audit relay, without an AI.Chat change**: its tool calls, `ToolInvoked` events and proposal execution results read `ai-tool-idempotency-denied`, `ai-tool-deadline-expired` and the other codes, with a breaking release-note row; the lease's own code stays in `IdempotencyCode`, for Lars to review. |
| LA6 | Built by lane LA (2026-10-25): **`AgentRunId` comes from the agent turn's run and is `null` outside a run** (a person's chat, a person's turn in the thread, a consultation, the grant check of a consultation proposal); it is not added to `NhAiInvocationContextBuilder` or the durable background-run adapter, whose `RunId` already is the operation, for Lars to review. |
| LA7 | Built by lane LA (2026-10-25): **no new sample case: SPM-308 stamps `ChangedByAgentRunId` from `AgentRunId` on the status event, and SPM-224 asserts the audited codes of the refused retry**; the real run path is proven in the AI.Chat agent run tests on both providers, for Lars to review. |
| LA8 | Built by lane LA (2026-10-25): **the LS1 test pins the infinite match timeout of the generated media type pattern by reflection and decides long valid and invalid values at the 128-character bound**, without a wall-clock assertion, for Lars to review. |
| LB1 | Built by lane LB (2026-10-25): **a durable cancellation request that the failed-step write finds wins over the step's own error**, as a third stop signal next to the fence and contention, and the step's error is logged as a warning; otherwise a handler error during a requested cancellation would be retried and the owner's cancellation lost (LS4), for Lars to review. |
| LB2 | Built by lane LB (2026-10-25): **a lost fence is the internal `NhBackgroundOperationOwnershipLostException`, a subclass of `InvalidOperationException` with the unchanged message**, so the step recording can tell it from a failed write; a release-note row covers tests that assert the exact type (LS4), for Lars to review. |
| LB3 | Built by lane LB (2026-10-25): **the column limits as internal constants next to the entity**: `NhBackgroundOperationColumnLimits` (100 and 300) feeds the runner's check, the entity attributes keep their literals so no public declaration, model or migration changes, and a provider test compares the constants with the EF model on both providers (LS3), for Lars to review. |
| LB4 | Built by lane LB (2026-10-25): **warnings, not errors, for the secondary write failure and for an oversized code or key**, with the operation, attempt and full text; the runner still logs the step's own error as an error when it fails the operation (LS3, LS4), for Lars to review. |
| LB5 | Built by lane LB (2026-10-25): **a cancelled step whose write finds the cancellation request logs nothing and rethrows the step's own cancellation**, because the request is expected and the runner records the operation as cancelled (LS4), for Lars to review. |
| LB6 | Built by lane LB (2026-10-25): **a cancellation that is neither the soft time-out nor a durable cancellation of the attempt, such as a handler's own client time-out, stays `TimedOut` (`soft-timeout`)**, and the success path is unchanged: a result returned after the soft time-out fired is still a time-out (LS5), for Lars to review. |
| LB7 | Built by lane LB (2026-10-25): **SPM-325 is a new case that runs the sample's existing project-analysis child** through Hangfire on the migrated PostgreSQL sample database with a heartbeat a minute apart and asserts that no heartbeat ran; LS3 and LS4 are proved by the library provider tests in its evidence, because a sample handler with oversized keys or a failing database would show what not to do, for Lars to review. |
| LB8 | Built by lane LB (2026-10-25): **not changed: a handler error thrown outside a step while the operation's cancellation is requested is still recorded as `handler-failed`** and may retry; closing that needs the same durable check on the runner's failure path and is left as a follow-up rather than widening LS5, for Lars to review. |
| LC1 | Built by lane LC (2026-10-25): **the three fixed patterns become internal generated regexes (`SelectorPattern`, `ExtensionPattern`, `ContentTypePattern`) without a timeout, and the selector keeps its 128-character bound**; the test pins `Regex.InfiniteMatchTimeout` on all three and matches valid and near-miss inputs of 100,000 to 200,000 characters without a wall-clock assertion (LS1), for Lars to review. |
| LC2 | Built by lane LC (2026-10-25): **the start check covers the profiles in code only, the chat profile, every agent's, and the skills' and triage's of agents in code, and fails host start**; agents and skills created in the administration are checked at save, and a version stored before keeps loading and fails per turn (LS7), for Lars to review. |
| LC3 | Built by lane LC (2026-10-25): **a turn is refused for its region when no profile of the chain permits the region together with the turn's data classification**, checked after the classification, so `assistant-classification-not-permitted` still wins when no profile takes the data; the new code is HTTP 400 and failure class `policy`, as the classification refusal (LS7), for Lars to review. |
| LC4 | Built by lane LC (2026-10-25): **the save check is a new field error `profile: region-not-permitted` (`NhAssistantFieldErrors.RegionNotPermitted`, translated in `en` and `nl`)**; a data skill's own profile is checked on every save and package import, an agent's profile only when it is new or changed (its stored and the default profile stay accepted, as for `not-found`), the run catalog still lists every chat profile, and the triage profile of run settings is not checked at save because a refused triage leaves the run undecided rather than failed (LS7), for Lars to review. |
| LC5 | Built by lane LC (2026-10-25): **the caps are public as `NhAssistantPresentationLimits` and the broken rules as `NhAssistantPresentationRules`**, so presenters can check them and the warning names a stable rule; a dropped display name warns too, each dropped result warns once per culture, and more supported cultures than the cap log one warning per host (LS8), for Lars to review. |
| LC6 | Built by lane LC (2026-10-25): **a shadow proposal is compared only with shadow proposals of the same agent in its shadow threads of the scope, by identity, open while its `ExpiresAt` lies ahead on the application's clock**; object keys, superseding and the rejection cooldown do not apply, because shadow proposals store no keys and are never decided, and live proposals are not compared (LS9), for Lars to review. |
| LC7 | Built by lane LC (2026-10-25): **the effect is recorded, not changed elsewhere**: the evaluation pairs runs by event and matches their proposals, so a variant run that repeats a finding now records nothing, like the live run that skipped it, instead of counting as a variant-only proposal; the feedback aggregates already exclude shadow proposals and do not change (LS9), for Lars to review. |
| LC8 | Built by lane LC (2026-10-25): **`SubjectChanged` is an init-only property of `NhAssistantProposalDecisionFacts`, not a ninth positional parameter**, so its constructor and deconstruction stay source compatible; it is computed for every pending proposal, also without `RequireUnchangedSubject`, and is `null` for a proposal that is no longer pending or when the default precondition lacks a recorded or current version; a replaced precondition always counts as knowing (LS16), for Lars to review. |
| LC9 | Built by lane LC (2026-10-25): **only an approval is refused as stale; a rejection is recorded as before**, as the decision-stage guards do; the check runs before those guards, in the execution's order, closes the proposal at stage `decision` and then tells a consultation's asker and starts a deferred proposal's follow-up run as the execution's close does (LS16), for Lars to review. |
| LC10 | Built by lane LC (2026-10-25): **the decision and the execution share one subject check (`NhAssistantSubjectChangeCheck`)**, and the decision facts run the application's precondition as well, read-only; SPM-271 now shows the approval refused at the decision, with a breaking release-note row (LS16), for Lars to review. |
| LC11 | Built by lane LC (2026-10-25): **the query token stays readable below `/hub` for the application's own hubs and is added only for hubs the libraries register**: an internal `INhLibraryHubPath` that the assistant registers from the same options its mapping reads, and the background-operations hub while its marker is registered; the root or a relative path never counts (LS27), for Lars to review. |
| LC12 | Built by lane LC (2026-10-25): **the background-operations `HubPath` validation, which still requires a path under `/hub`, is left as it is** to stay out of lane LB's file; now that the token follows the configured path it could be relaxed in a follow-up (LS27), for Lars to review. |
| LC13 | Built by lane LC (2026-10-25): **the hub test connects a real SignalR client to the assistant hub at `/hubs/assistant` with the token in the URL**, through a JWT bearer whose message handler is the platform's (`NhHubQueryTokens.Read`, visible to the AI.Chat tests), and the platform's own bearer setup is tested in AspNet.Common; the sample keeps its default hub path and documents it next to its prefix (LS27), for Lars to review. |
| LC14 | Built by lane LC (2026-10-25): **the editor title follows a language switch and the import dialog names a package agent the host lacks by the package's own source and display name**; `agentDisplayName` stays internal to the administration, with no new Angular export (LS26), for Lars to review. |
| LC15 | Built by lane LC (2026-10-25): **SPM-323 proves the start check with the sample assistant moved to `eu-west` in a derived host, and SPM-324 the shadow duplicate with the project closer**; the save and turn refusals of LS7 are proved by the library tests on both providers, and LS8, LS16, LS26 and LS27 extend SPM-246, SPM-268, SPM-271, SPM-253 and SPM-263, for Lars to review. |
| RH1 | Built by lane RH (2026-10-25): **a reserved share is a lease of its own per queue, `processor:{key}:reserved:{queue}` with the reserved slots, next to the shared `processor:{key}` lease with the slots the reservations leave**; an operation on a reserving queue tries its reserved lease first and the shared one next, any other operation only the shared one, so other queues never take a reserved slot and the slots in use never exceed the global limit; without reservations the shared lease keeps every slot, as before (LS10), for Lars to review. |
| RH2 | Built by lane RH (2026-10-25): **host start fails for a reservation without a global limit, for reservations that leave no shared slot (they must stay below the global limit), and for a reservation above the queue's own `WithQueueConcurrency` limit**; queue names are normalized as `UseQueue` does, and every process of a `ProcessorKey` needs the same reservations (LS10), for Lars to review. |
| RH3 | Built by lane RH (2026-10-25): **`Queue` moves all six assistant operation types (runs, both proposal executions, revalidations, deferred trigger fires, replays) to one queue, and `MaxConcurrency(n)` is that queue's limit for all of them together, not a limit per operation type**; it needs `Queue`, refuses to change a different limit the queue already has, and the parameterless `AddAssistantAgentRuns()` stays as an overload (LS10), for Lars to review. |
| RH4 | Built by lane RH (2026-10-25): **`RunSchedules` is read from the bound options when the host starts, so the schedule registration and the data schedule sync stay registered and do nothing when it is false**, because registration cannot see configuration; neither resolves `IRecurringJobManager` or `JobStorage` then. The run configuration refresh keeps running, so the host still follows data changes for its triggers, runs and consult tools (LS11), for Lars to review. |
| RH5 | Built by lane RH (2026-10-25): **`InputId` is an init-only property of `NhAssistantTriggerReceipt`, and the HTTP receipt DTO of `threads/start`, the webhooks and run-now is unchanged**, because the lookup is a server API; a rejected, ignored, deduplicated or resume fire names no input, except that the internal receipt of a retried deferred fire names the input it wrote before (LS13), for Lars to review. |
| RH6 | Built by lane RH (2026-10-25): **when several inputs hold the key, the lookup returns the first by its creation time and id**, the event's first delivery; it searches the agent's live and shadow threads and says which (`Shadow`), and the keys of candidate versions differ from the event's, so they are not found (LS13), for Lars to review. |
| RH7 | Built by lane RH (2026-10-25): **while an input waits, `RunId` is the run of its thread that records this event's key (the run the event started, or a resume's run), and `null` for a waiting event that coalesced into another event's run**; once a run claimed the input it is that run, with its status in `RunStatus` (LS13), for Lars to review. |
| RH8 | Built by lane RH (2026-10-25): **`FindByInputAsync(inputId)` next to `FindByEventAsync`, both with an optional tenant**: without a tenant the lookup is the application's and spans every tenant; with one it keeps the thread's tenant, or the whole application's thread when that tenant takes part, filtered in SQL as `NhAssistantWholeApplicationTenancy.Reaches` decides (LS13), for Lars to review. |
| RH9 | Built by lane RH (2026-10-25): **the migration `TriggerEventLookup` adds one index, `IX_AssistantQueuedInput_EventKey_TriggerId`, and none on `AssistantRun`**; the agent comes from the join with the thread, and the run of a waiting event is read per thread through the existing index (LS13), for Lars to review. |
| RH10 | Built by lane RH (2026-10-25): **SPM-326 is a new case: `SampleAgentRunCapacityComposition` (queue `agent-runs`, two of the eight slots reserved, at most four) in `Program.cs` and the agent run sample host, and the maintenance page's `GET agent-maintenance/intake-reviews/{eventId}` through `SampleIntakeReviewLookup`**; `RunSchedules` is not shown in the sample, which has one host, and the reservation's behavior and the lookup's fires are proved by the library tests on both providers (LS10, LS11, LS13), for Lars to review. |
| MP1 | Built by lane MP (2026-10-25): **`INhAssistantMcpServers` (namespace `NewHeap.Platform.AI.Chat.AspNet.Mcp`) returns the administration's own DTOs (`NhAssistantMcpServerDto`, `NhAssistantMcpToolDto`, `NhAssistantMcpServerTestResultDto`), and its requests mirror the internal inputs field for field (`NhAssistantMcpServerRequest`, `NhAssistantMcpOAuthRequest`, `NhAssistantMcpReaderRequest`)**; the tool request is `NhAssistantMcpToolSettingsRequest` because `NhAssistantMcpToolRequest` is an internal type of AI.Chat, there is no `GetAsync` beside `ListAsync`, and the DTO mapping moved from the endpoints to one internal mapper both use (LS28), for Lars to review. |
| MP2 | Built by lane MP (2026-10-25): **an assignment writes only its own row: the agent's version, `IsOverridden`, `UpdatedAt` and `UpdatedBy` stay**, so an agent in code keeps following its code and neither an open editor nor a package import conflicts; MCP assignments are no part of a package or its locks and an import keeps them. The price: the agent editor's save still replaces the whole list, so an editor opened before an assignment drops it when it saves; both are audited (LS28), for Lars to review. |
| MP3 | Built by lane MP (2026-10-25): **assign and unassign are idempotent and return the server**: a call that changes nothing succeeds without an audit; an unknown server is `assistant-mcp-server-not-found` (checked first), an unknown agent `assistant-agent-not-found`, and a 33rd server the field error `mcpServerIds: too-long` (`NhAssistantMcpServerLimits.MaxServersPerAgent` = 32, the editor's limit) (LS28), for Lars to review. |
| MP4 | Built by lane MP (2026-10-25): **the new audit kind is `AdminMcpServerAssignmentChanged = 90`** with `ObjectId` the server, `AgentId` the agent and `ResultCode` `assigned` or `unassigned`; 90 opens a new decade so it stays clear of values other lanes of train 22 add after 83 (LS28), for Lars to review. |
| MP5 | Built by lane MP (2026-10-25): **the service does not authorize and takes the actor as an argument, as `INhAssistantAgentPackages` does**; a blank or overlong actor throws `ArgumentException`, `TestAsync`'s actor is optional as in the administration, and no endpoint changed (LS28), for Lars to review. |
| MP6 | Built by lane MP (2026-10-25): **`AllowLoopback` holds in every environment except Production, and Development needs no option**; besides the start check in Production, the host guard itself ignores the option in Production in case the start check does not run (LS29), for Lars to review. |
| MP7 | Built by lane MP (2026-10-25): **the OAuth redirect rule follows the same switch**: where loopback is allowed, `RedirectUri`, `ClientUri` and `ReturnOrigins` may be http on loopback; `ClientMetadataDocumentUri` stays https only, and the start error names `AllowLoopback` (LS29), for Lars to review. |
| MP8 | Built by lane MP (2026-10-25): **`INhAssistantInstructionAssetUsage` lives next to the asset DTOs in `NewHeap.Platform.AI.Chat.AspNet.AgentRuns` and refuses an id that is not lowercase dash-case of at most 128 characters with `assetId: invalid`**, so SQL Server's case-insensitive comparison finds nothing PostgreSQL would not; it lists every skill version whatever its status, history included, and agents only when `InstructionsFromAsset` is set, so an agent whose own text has the id `<agent>-instructions` is never listed; no endpoint, no index and no migration, ordered ordinal in memory (LS30), for Lars to review. |
| MP9 | Built by lane MP (2026-10-25): **`GetPackageAsync` is `null` for an unknown or malformed package id and groups the links per agent (`NhAssistantPackageAgentLinksDto`) with `ChangedLocally` as the administration computes it**; once every agent is unlinked the package stays with empty `AgentIds` and `Agents` (LS32), for Lars to review. |
| MP10 | Built by lane MP (2026-10-25): **`ReadAsync` shares the export's id checks and builders (`ExportAgentAsync` with `HashesOf`) and returns the agents in the order given, without locks**; unlike an export it reads agents of different packages together, and it returns `TaskResult` with the export's failures (LS33), for Lars to review. |
| MP11 | Built by lane MP (2026-10-25): **SPM-331 is one case for LS28 to LS30, LS32 and LS33 with `SampleAssistantOperationsService`, and its LS29 evidence is a sample host of the environment `Testing`** (`AssistantSampleHost.EnvironmentName` became virtual) that reaches the milestones server only with `AllowLoopback`; the package comparison uses a package the test exports and installs, then a description changed on the host, for Lars to review. |
| DN1 | Built by lane DN (2026-10-25): **the per-agent limit is the asking agent's `MaxConsultationsPerTurn(count)` on `NhAssistantAgentRunBuilder` in code and replaces the application's limit, higher or lower**; an agent created in the administration keeps the application's limit, because a stored value would need a migration, and both limits are 1 to 100 (LS22), for Lars to review. |
| DN2 | Built by lane DN (2026-10-25): **every consultation a turn starts counts, also one refused later in preparation (depth, subject, budget), because the turn's check runs first**; a consultation outside a turn, such as one through MCP, has no turn to count, and `assistant-consult-turn-limit` is HTTP 400 like `assistant-consult-limit-reached` (LS22), for Lars to review. |
| DN3 | Built by lane DN (2026-10-25): **`awaiting-application` lists until `ExecutionReportDueAt` and by it, `execution-unknown` has no time bound and lists by `DecidedAt` (otherwise `CreatedAt`)**; grouped pages use the same time as their first expiry, an empty `status=` is the default, and the statuses keep the approval statuses' names in `NhAssistantInboxStatuses` (LS14), for Lars to review. |
| DN4 | Built by lane DN (2026-10-25): **`INhAssistantProposalCounts.CountAsync(NhAssistantDecidingPerson, statuses)` returns a dictionary by status with zero for none**, counts a repeated status once, throws `ArgumentException` for another status, and counts with the inbox's own query, one count and one live evaluation of the whole application per status (LS14), for Lars to review. |
| DN5 | Built by lane DN (2026-10-25): **`INhAssistantSystemMembers.ListAsync(Guid? divisionId)`, `null` for the whole application app-wide, scoped and registered by `Members<T>()`**; the notifier reads at most 200 listed people next to the 50 people of the thread and skips empty ids and ids over 256 characters, while the member snapshot, the inbox and the summaries stay as they are (LS15), for Lars to review. |
| DN6 | Built by lane DN (2026-10-25): **`AlsoInline` acts on a person's own decision turn only, in a chat or a request in an agent thread**; a run already defers proposals of such tools and the asker-decides route of a consultation stays refused for them (LS17), for Lars to review. |
| DN7 | Built by lane DN (2026-10-25): **a chat's hand-off names the proposal's first target as its subject, otherwise `nh-system/global`, and the conversation's tenant**; a report reaches a chat proposal only in that tenant, and the audit of a chat proposal names no subject (LS17), for Lars to review. |
| DN8 | Built by lane DN (2026-10-25): **in a chat nobody is notified and no follow-up run starts, and every close is posted into the chat: an outcome sentence with the code and the escaped summary**, in the request's culture during the turn and the application's default culture for reports and deadlines; an agent thread keeps the summary-only post and the notifications. The person who approved resolves an uncertain chat proposal (DN13) (LS17), for Lars to review. |
| DN9 | Built by lane DN (2026-10-25): **the tool call keeps waiting as a deferred call does until the report ends it, and the turn's tool result is `assistant-proposal-handed-to-application` with `reportDueAt`**, the refusal's code or `hand-off-failed` with tools disabled, or the code of a report the executor made at once (LS17), for Lars to review. |
| DN10 | Built by lane DN (2026-10-25): **a host without agent runs whose application executes a tool `AlsoInline` runs only the report expiry in the run maintenance**, so a chat hand-off still becomes uncertain at its deadline without the rest of the maintenance (LS17), for Lars to review. |
| DN11 | Built by lane DN (2026-10-25): **SPM-327 combines the digest and the status filter with `SampleSystemMembers` (the administrator role and roles that grant `app.project.manage`, no division lists), SPM-328 is the order in a person's chat, and LS22 extends SPM-278 with an application limit of two**; the sample's consulting agent is the chat assistant without run settings, so the per-agent limit is proven by the library tests only (LS14, LS15, LS17, LS22), for Lars to review. |
| DN12 | Built by lane DN (2026-10-25): **Angular gains `AgentInboxFilter.status`, `AgentInbox.status` and `AgentInboxStatus` in the API models and the mock API, without a status choice in the inbox components or the sample portal**, which belong to the front-end train (LS14), for Lars to review. |
| AG1 | Built by lane AG (2026-10-25): **`UpdateAsync` takes a new `NhAssistantAgentDefinitionRequest`, not `NhAssistantAgentRequest`**: it replaces the six fields of the definition and keeps every optional field (`null`), which the create request cannot say for `OfferedInChat`; `UpdateAsync` and `ResetAsync` return the agent as `INhAssistantAgentDirectory` reads it (`NhAssistantAgentInEffectDto`), and the actor id is checked as the other services check it (LS23), for Lars to review. |
| AG2 | Built by lane AG (2026-10-25): **the MCP assignments are kept by the store in the same save**: an internal `KeepMcpServers` on the administration's input passes no server list to the update, so a change of the assignments between a read and the update is not undone, and whether an agent in code is overridden is decided with the assignments it keeps; the endpoint `PUT admin/agents/{id}` still replaces them (LS23), for Lars to review. |
| AG3 | Built by lane AG (2026-10-25): **both services live in `NewHeap.Platform.AI.Chat.AspNet` next to `INhAssistantAgentConfiguration` and `INhAssistantAgentControl`, and authorize nothing**: they are server APIs the application calls for an administrator or process it authorized, and the directory returns the instructions in full; the endpoint's policy check (`IAuthorizationPolicyProvider`) and required fields are checked by `UpdateAsync` itself (LS23, LS24), for Lars to review. |
| AG4 | Built by lane AG (2026-10-25): **`NhAssistantAgentInEffectDto` embeds the agent's whole `NhAssistantAgentState` as `State`, next to `RunState`, `InShadow` and `ShadowOf`**; instructions are `null` with `InstructionsProblem` when their asset does not resolve, names and descriptions of agents in code stay keys, and `NhAssistantAgentDirectoryFilter` narrows by `Source` and `IncludeShadowVariants` (true by default) (LS24), for Lars to review. |
| AG5 | Built by lane AG (2026-10-25): **an entry declares `IndexBy` or `IndexByEach`, never both (refused at registration)**; each value is normalized as `IndexBy` normalizes, repeats are stored once and ordered, the 256-character bound is checked after normalization, more than 50 values are `value: too-long`, and the main `IndexValue` column stays empty for an `IndexByEach` entry (LS25), for Lars to review. |
| AG6 | Built by lane AG (2026-10-25): **values stored before an entry declared `IndexByEach`, or before its selector changed, are reindexed at the process's first read of the agents (the seeder)**, row by row in a context of their own guarded by the row's version and value, so a save at the same time or another host wins; a value that no longer reads as the type or no longer fits keeps its values, and `IndexBy` keeps having no backfill (LS25), for Lars to review. |
| AG7 | Built by lane AG (2026-10-25): **a promotion copies the description, instructions, tool selectors, required policy and autonomy, but not the display name**: the live agent keeps its name, since the variant's name tells administrators the variants apart, and its MCP assignments and metadata; an agent created at runtime also takes the variant's profile, classification, what it works on and its asset reference, and whether it is offered in the chat never copies (LS31), for Lars to review. |
| AG8 | Built by lane AG (2026-10-25): **run settings are never copied, also not onto an agent created at runtime**: the update path does not change run settings, and the variant's run settings exist to run it in shadow, so the live agent keeps whom it answers to, its budgets and its approval policy (LS31), for Lars to review. |
| AG9 | Built by lane AG (2026-10-25): **moved skills go through `SaveSkillAsync`**: each live version of a variant's data skill becomes the next live version of the live agent's skill of the same id, or a new skill, which starts in shadow as every new skill does; a skill the live agent has in code refuses the promotion before anything changes, and a later failure stops there with the variant kept, because the definition, the skills and the deletion are separate saves (LS31), for Lars to review. |
| AG10 | Built by lane AG (2026-10-25): **HD10 at the promotion: the live agent must still be allowed data variants now** (the same check as at creation), otherwise `assistant-shadow-variants-not-allowed`; a variant whose asset instructions do not resolve is refused with `instructionsAsset: invalid`, and onto an agent in code the asset's verified text copies as text, since agents in code take no asset (LS31), for Lars to review. |
| AG11 | Built by lane AG (2026-10-25): **the promotion's own audit kind is `AdminAgentShadowVariantPromoted` = 91, because lane MP took 90**, the new code `assistant-not-shadow-variant` is 409, `NhAssistantShadowVariantDto.Version` lets the administration send the variant's version, and `POST admin/agents/{id}/promote` answers `NhAssistantShadowVariantPromotedDto` with the live agent as the agent list shows it (LS31), for Lars to review. |
| AG12 | Built by lane AG (2026-10-25): **SPM-329 and SPM-330 run through one `SampleAgentDefinitionService`, and `IndexByEach` extends SPM-313 with a `departments` entry on the planner** instead of a third case, which the portal shows without an editor of its own; the Angular confirmation of a promotion is inline in the variant's card, not a modal, and the playground offers it through its existing ceiling of shadow variants (LS23 to LS25, LS31), for Lars to review. |
| DN13 | Built by lane DN (2026-10-25): **the person who approved a chat proposal resolves its uncertain hand-off with `POST conversations/{id}/approvals/{approvalId}/resolve` and the body of an agent thread's resolve (`NhAssistantResolveExecutionRequest`), and the answer is the conversation**: the rules of AE13 apply (`execution-unknown` only, `hand-to-application` for a handed-off proposal after the execution guards with a new attempt and deadline, `mark-resolved`, the tool's approve policies checked live, the audit `AgentProposalResolved`), `re-execute` is refused because a chat proposal runs in its function only within the person's turn, another participant gets `assistant-approval-forbidden`, and an agent thread's proposal answers `assistant-approval-decision-invalid` because its approvers resolve it through `approvals/{id}/resolve`; the approval part says `CanResolve` only to that person, so the card shows the choice to nobody else (LS17), for Lars to review. |
| DT1 | Built by lane DT (2026-10-25): **both kinds run, the synchronous factories first and then the asynchronous ones, each in registration order and each receiving the transaction the one before returned, and CAP registers only its asynchronous factory**, so an application's synchronous factory opens the transaction and CAP wraps it whatever the registration order, and no registered factory is ignored; a synchronous factory registered after `AddCap` now receives `null` instead of CAP's transaction, and `GetServices<NhTransactionFactory>()` no longer returns CAP's, both with a release-note row (LS38), for Lars to review. |
| DT2 | Built by lane DT (2026-10-25): **`NhTransactionFactoryAsync` returns `Task<IDbContextTransaction>` and takes the synchronous delegate's parameters plus the token: `(prev, dbContext, serviceProvider, cancellationToken)`**, next to `NhTransactionFactory` in `NewHeap.Platform.AspNet.Common.DAL`, a `Task` as EF's `BeginTransactionAsync` returns rather than a `ValueTask` (LS38), for Lars to review. |
| DT3 | Built by lane DT (2026-10-25): **`StartTransactionAsync` checks the token before any factory runs, also when only synchronous factories are registered, and disposes the transaction an earlier factory returned when a later factory throws or is cancelled**, so the context never keeps a transaction a later scope would join without owning it; a failing factory disposes only a transaction it opened itself. The early check has a release-note row (LS38), for Lars to review. |
| DT4 | Built by lane DT (2026-10-25): **CAP's factory checks the token, chooses the provider's CAP transaction before anything opens, opens with EF's `BeginTransactionAsync(cancellationToken)` and only then sets `ICapPublisher.Transaction` and `CapTransactionScope.Current`**, so a cancelled or failed open leaves the scope as it was; it does not use CAP's own `BeginTransactionAsync(publisher)` extension, which waits on the opening synchronously in CAP 8.4.1 (LS38), for Lars to review. |
| DT5 | Built by lane DT (2026-10-25): **`CapEFDbTransaction` keeps the EF transaction it wraps and disposes it itself, asynchronously in `DisposeAsync`, and then lets CAP release its reference without a second dispose**, because `ICapTransaction` has no asynchronous dispose in CAP 8.4.1; the synchronous `Dispose` does the same, so after a SQL Server commit, where CAP's commit diagnostics had dropped the EF transaction undisposed, both disposals now dispose it, and `Instance` reads the kept transaction (LS38), for Lars to review. |
| DT6 | Built by lane DT (2026-10-25): **the provider tests live in `NewHeap.Platform.AspNet.Common.Tests`, which now references `NewHeap.Platform.Events.Cap`, without a new package or test project**: CAP is not started, keeps its real outbox tables in the test's own database, hands events to a recording dispatcher instead of a broker, and on SQL Server gets its commit diagnostics subscribed as a started host does; cancellation while opening is an interceptor that cancels the caller's token when the provider is about to begin the transaction (LS38), for Lars to review. |
| DT7 | Built by lane DT (2026-10-25): **no SPM-337: the existing CAP cases are extended**, because the sample already opens its unit of work with `AddCap` and `StartOrGetTransactionScopeAsync(cancellationToken)`, which now opens asynchronously without a change; SPM-195 (outcome amended), SPM-200 and SPM-191 list the library provider tests as evidence, and `Program.cs` says how the transaction opens (LS38), for Lars to review. |
| AX1 | Built by lane AX (2026-10-25): **the strict check is an init-only `StrictStructuredOutput`, off by default, on both `NhAiDeterministicChatClient` and `NhAiScriptedChatClient`, next to a public `NhAiStrictStructuredOutput.Validate` that returns every `NhAiStrictSchemaViolation` with its JSON pointer**; a refused request throws `HttpRequestException` with status 400, which the executor treats as a non-transient provider error (`ai-model-dependency-unavailable`, no retry), is still recorded and consumes no queued response, and a stream throws when it starts (LS37), for Lars to review. |
| AX2 | Built by lane AX (2026-10-25): **exactly the three decided rules**: a `$ref` is `#` or `#/$defs/name` naming an entry of the root's `$defs` (a missing entry, a pointer into a definition, `definitions` and every other pointer are refused), every object schema (type `object`, alone or with `null`, or properties without a type) sets `additionalProperties: false`, and every property is in `required`; no rule for the root's type, keywords next to `$ref` or formats (LS37), for Lars to review. |
| AX3 | Built by lane AX (2026-10-25): **the guidance names what Microsoft.Extensions.AI 10.9.0 actually generates, pinned by tests**: a repeated object type is written out at each property and a nullable object uses `["object", "null"]` (both accepted with closed objects), recursion at the root refers to `#` (accepted), while a list element type used twice and recursion below the root refer by JSON pointer (refused), and `ChatResponseFormat.ForJsonSchema<T>()` without transform options leaves every object open (refused); so the finding's repeated types show up as repeated list element types (LS37), for Lars to review. |
| AX4 | Built by lane AX (2026-10-25): **SPM-335 is a new case: `ProjectAiStatusDigestService` with the sample's own `ProjectAiStructuredOutput`** (strict transform options, then every JSON-pointer reference target moved into `$defs`), proved with the strict deterministic client, plus the digest's `ai-model-timeout` at the run's deadline; LS40 extends SPM-225 with `AiAgentHistorySamplesTests` and LS39 extends SPM-232's library evidence (LS37, LS39, LS40), for Lars to review. |
| AX5 | Built by lane AX (2026-10-25): **every `OperationCanceledException` while the caller's token is not cancelled is `ai-model-timeout`**: the profile timeout, the invocation deadline and also the provider client's own timeout, such as `HttpClient.Timeout`, because the caller asked for no cancellation; a retry-eligible profile still retries a timed-out attempt first, and a deadline that passes during the stream's retry delay is a timeout too, where it threw out of the stream and recorded `Succeeded` before (LS39), for Lars to review. |
| AX6 | Built by lane AX (2026-10-25): **"budget released" is verified as one reservation and one usage record with the same `ModelCallId` per call, for a timeout and a caller's cancellation alike**, because `INhAiBudgetManager` has no release call and a manager settles a reservation by the usage record; "audit completes" is that usage record plus the `ai.model.invoke` activity's outcome and finish reason tags, since the executor writes no `NhAiAuditRecord` (LS39), for Lars to review. |
| AX7 | Built by lane AX (2026-10-25): **the adapter keeps the code on a new public `NhAiAgentModelCallException : InvalidOperationException` with `Code` (`ai-model-call-failed` when the result names none) and the unchanged messages, for every failed call, failed stream start and failed completion**, not only timeouts, so `catch (InvalidOperationException)` keeps working; a release-note row covers assertions of the exact type, as LB2 did (LS39), for Lars to review. |
| AX8 | Built by lane AX (2026-10-25): **the assistant maps `ai-model-timeout` to the codes a timeout gave before: `assistant-turn-timeout` in a turn and `assistant-consult-timeout` in a consultation**, with one catch clause each in the turn runner; the triage needs no change and stays undecided, and every other failed code keeps `assistant-model-unavailable`. Not changed: the triage's own response schema lacks `additionalProperties: false`, so a provider in strict mode would refuse it; that is left to the chat lanes (LS39, LS37), for Lars to review. |
| AX9 | Built by lane AX (2026-10-25): **the slots are `ChatHistoryProviderFactory` (`Func<IServiceProvider, ChatHistoryProvider>`) and `AIContextProvidersFactory` (`Func<IServiceProvider, IEnumerable<AIContextProvider>>`)**, named after 1.19.0's `ChatHistoryProvider` and `AIContextProviders`, which are instances shared by the agent's sessions rather than factories; the adapter calls them once per created agent with the services passed to `CreateAsync` and refuses a `null` provider, and they take no NewHeap context because the application builds the request (LS40), for Lars to review. |
| AX10 | Built by lane AX (2026-10-25): **tool publication stays NewHeap's through a guard: each AI context provider runs wrapped, and one that returns a tool the context did not already hold fails the run with `InvalidOperationException` before the model is called**, while its instructions and messages pass; the history provider is passed as it is and the conflict options keep Agent Framework's defaults; compaction (`CompactionProvider`) is experimental in 1.19.0 (`MAAI001`), so the sample shows the history provider only (LS40), for Lars to review. |
| FE1 | Built by lane FE (2026-10-25): **the shell handles Escape on keydown for the topmost modal that can close and calls the content's new optional `INhModalComponent.onModalEscape`, else closes as its close button does**; mutating and confirmation content call `closeDialog` there, so an override still runs, and their `document:keyup` listeners are gone (`onKeyUp` stays, deprecated and unbound); a `closeable: false` modal now ignores Escape, also with mutating content, and an Escape a control already prevented or a repeated key is left alone, with breaking release-note rows (LS41), for Lars to review. |
| FE2 | Built by lane FE (2026-10-25): **the close label is `nh-common.modal.close` from a new bundle of the main entry point, `NH_COMMON_TRANSLATIONS` (English, Dutch)**, which the shell reads itself, the application's translation first, without `TranslateService` being required and without a merger or a `forRoot` change; no JSON asset or package export was added (LS41), for Lars to review. |
| FE3 | Built by lane FE (2026-10-25): **initial focus goes to the content's `autofocus` element, else its first control, else the close button, else the holder (`tabindex="-1"`, no outline)**; content that renders its fields after loading gets its first field focused by a DOM observer unless the user moved focus, and `focusInitial()` is public; Tab is kept inside by a keydown handler (one radio per group, disabled, hidden and inert controls skipped) without sentinels or `inert` on the page; `role="dialog"` stays on the holder (LS41), for Lars to review. |
| FE4 | Built by lane FE (2026-10-25): **an internal root `NhModalStack` decides the topmost modal, so modals created outside `NhModalService` take part too**; focus returns to the opener only while focus is in the closing modal or nowhere, otherwise to the modal now on top; `nh-modal-open` leaves the body only with the last modal, and a second `close()` is ignored. `NhModalService.modalRefs` is unchanged: a modal its host page destroys stays listed, as before (LS41), for Lars to review. |
| FE5 | Built by lane FE (2026-10-25): **SPM-115 is extended, not a new case: the portal's edit modal needs no code of its own**, so its evidence is the shell, the 16 Karma specs in Chrome (Tab, Shift+Tab, Escape, disabled controls, stacked dialogs, route destruction, deferred focus) and the portal build; the keyboard was not exercised in a running portal, which needs a signed-in session against the sample API (LS41), for Lars to review. |
| FE6 | Built by lane FE (2026-10-25): **`nh-assistant-document` splits the document at top-level diagram fences, so a diagram inside a list stays code**; a sibling link keeps its resolved bundle path as `href` and its click is prevented, a query is ignored, a link to the document itself becomes an anchor, `/` resolves from the bundle root, heading ids are `nh-document-{n}-{slug}` with GitHub-style fragments, line breaks follow Markdown (`breaks: false`, unlike the chat), and `headingsChange` fires after render next to a `headings` signal and `scrollToHeading` (LS42), for Lars to review. |
| FE7 | Built by lane FE (2026-10-25): **`NhAssistantDiagramRenderer` is `{ languages, render(source, language): Promise<string> }`, with the lazy loading inside `render` and no separate loader type**; the SVG passes DOMPurify's SVG profile without `a`, `image`, `use`, `feImage` and `foreignObject`, styles and attributes with escapes, `@import`, `@font-face` or a `url()` other than a fragment are emptied or dropped, the drawing goes into a shadow root, and the reason is cut at 300 characters; Mermaid therefore needs `htmlLabels: false` (LS42), for Lars to review. |
| FE8 | Built by lane FE (2026-10-25): **the document's texts are `nh-assistant.document.*` through the translate pipe like the other assistant components**, so they come with `provideNhAssistant`; an application that shows documents without it starts `NhAssistantTranslationMerger` or translates the keys, and the existing chat renderer only exports its tag list and escape function for reuse (LS42), for Lars to review. |
| FE9 | Built by lane FE (2026-10-25): **SPM-336 uses a tiny renderer of the sample's own `flow` language (`A -> B` per line) because the sample has no diagram package**; its drawing module is a dynamic import, the handbook is a new page `/management/handbook` with three English documents, one with a broken diagram, and its interface texts are in English and Dutch (LS42), for Lars to review. |
| FE10 | Built by lane FE (2026-10-25): **the profile is `public-website` with `frontend: "public"`, `--authentication` refused, persistence only when chosen, `NhCommonModule.forRoot` required and the portal's identity, API-base and modal checks dropped**; the pins are the lowest version every `@angular/*` peer range of platform-common accepts (`^`, `~`, `>=`, exact, `||`), read from the new generated `npmPeerDependencies` of `distribution.json` and the install lock, with `^major.minor.0` tooling and TypeScript and zone.js per supported major; verified with network access, not offline, against npmjs.org and the NuGet sources with their local caches: the generated site resolves and installs Angular 20.3.28 with every peer, 20.3.27 fails with `ERESOLVE`, `dotnet restore` succeeds and the foundation inspection is clean; build and start were not run because the bootstrap creates no Angular application (LS43), for Lars to review. |
| CT1 | Built by lane CT (2026-10-25): **the person a turn authorizes is an internal ambient `NhAiPersonInvocation` in AI.Common (principal, the invocation context resolved once at the start, and the resource the request gate would build), which the turn makes ambient for its whole flow**; the request gate authorizes every tool policy against that principal and returns that context without resolving the request again, and the agent gate's initiator of a request turn, the API bridge's discovery, the consultation tools and personal MCP servers read it before the request, so a tool never authorizes a request that ended or that belongs to someone else (LS18, LS19), for Lars to review. |
| CT2 | Built by lane CT (2026-10-25): **with `KeepTurnsAfterDisconnect` the request's pipeline still waits for the turn**: the stream reads and drops the events after the disconnect and the endpoint returns when the turn ends, so the request's services stay alive and no services of its own are needed; only the cancellation and the authorization stop depending on the request. The option is read per request for messages and decide, also for a person's turn in an agent thread, whose tools the agent gate authorizes with the captured initiator (LS18), for Lars to review. |
| CT3 | Built by lane CT (2026-10-25): **`INhAssistantConversationTurns` takes `NhAssistantDecidingPerson` and no new person record, and the person's active division is `NhAssistantConversationTurnOptions.ActiveDivisionId`, as a request header names it per request**; it counts only when the host maps the active division and the person passes its policy, and division policies read it from an internal resource derived from `NhAiAspNetScopeAuthorizationResource`, which is no longer sealed, that implements `INhDivisionScopedResource` (LS19), for Lars to review. |
| CT4 | Built by lane CT (2026-10-25): **the person's context comes from the application's `INhAiAuthenticatedInvocationContextResolver`, run over a `DefaultHttpContext` that carries only the principal, and its actor id and tenant must equal the person's**, otherwise `assistant-context-unavailable`; a resolver that needs more of a request than the principal refuses the person. No new error code (LS19), for Lars to review. |
| CT5 | Built by lane CT (2026-10-25): **every turn of the service runs with the caller's execution context suppressed and in a DI scope of its own that ends with the turn**, so no ambient request, principal or credential of the caller (a webhook, a service account or another person) reaches the turn; in return API bridge tools are not offered (they forward the person's own request credential) and MCP servers that forward the person's sign-in answer unauthorized, while tools on the inner discovery policy work (LS19), for Lars to review. |
| CT6 | Built by lane CT (2026-10-25): **the service checks the endpoints' rules in their order: disabled, the person's context, an agent thread (`assistant-conversation-not-found`, chats only), the store's conversation access (the owner whatever the tenant, a participant only in their tenant), the agent, and the assistant's access policy together with the agent's policy as `assistant-agent-forbidden`**; the access policy is evaluated with the person's resource, so a division access policy works without a request (LS19), for Lars to review. |
| CT7 | Built by lane CT (2026-10-25): **`KeepTurnAfterCallerLeaves` is a `bool?` whose `null` follows the host's `KeepTurnsAfterDisconnect`**; the caller's token always ends the wait with `OperationCanceledException`, and cancels the turn unless it is kept, in which case the turn finishes in its own scope and a failure after the caller left is logged (LS19), for Lars to review. |
| CT8 | Built by lane CT (2026-10-25): **`NhAssistantConversationTurnResult` reads the stored conversation after the turn: the status and code of the turn's last event, the turn's assistant message as `NhAssistantMessageDto`, the pending approval as a list of `NhAssistantApprovalPartDto` (at most one in a chat) and the usage**; a refusal before the turn is a failed result with the endpoint's code, a turn that started and failed a result with status `failed` (LS19), for Lars to review. |
| CT9 | Built by lane CT (2026-10-25): **the channel is a required dash-case code of `NhAssistantConversationTurnOptions`, validated as the decision service's, and stamped on every audit event of the service's turns through the turn scope (`ToolInvoked`, `ApprovalRequested`, `ApprovalApproved`, ...)**; the conversation endpoints' turns keep `Channel` null instead of recording `web`, so their audit does not change (LS19), for Lars to review. |
| CT10 | Built by lane CT (2026-10-25): **`DecideAsync` decides the chat's own approval and a consultation's proposal the person decides there as the asker, approve or reject only (no `leave`)**; the endpoint's asker check moved to an internal `NhAssistantAskerDecisions` that both use, with the person's principal for the approver check (LS19), for Lars to review. |
| CT11 | Built by lane CT (2026-10-25): **SPM-332 is a new case: `SampleAssistantMessagingChannel` with `POST assistant-channel/links` (signed in), `messages` and `decisions` (the link token, no sign-in), the person rebuilt from the user store as in SPM-268, the delivery id as the client message id and `KeepTurnAfterCallerLeaves` on; the sample sets `KeepTurnsAfterDisconnect` in Development**, and the disconnect itself is proved by the library tests on both providers, as the sample tests run on PostgreSQL only (LS18, LS19), for Lars to review. |
| CT12 | Built by lane CT (2026-10-25): **no migration, no new error code and no Angular change**: the client already loads a conversation that is still running and follows it live; the assistant UI rule documents that a kept turn and a channel's turn appear through live updates and the next snapshot (LS18, LS19), for Lars to review. |
| HT1 | Built by lane HT (2026-10-25): **the end of a heartbeat admits the attempt's own writes that waited for it, before the next beat can start**; under load a beat took longer than `HeartbeatInterval`, the next one was due at once and re-entered the write gate before the waiting write could, so a message, signal suspension or completion waited behind every beat and the attempt never ended (`A_heartbeat_skips_...`). The gate stays internal, the heartbeat still never waits for the lock behind an own write, and the release note has a `None` row, for Lars to review. |
| HT2 | Built by lane HT (2026-10-25): **a budget reservation whose increment found no ledger repeats the increment once the ledger exists, and only an increment against a ledger that already existed refuses it**; a concurrent first reservation of the day that created the ledger between the increment and the existence check made the others read the budget as exhausted (`Concurrent_reservations_never_exceed_the_daily_budget`). The fix is in AI.Chat `Governance`, outside the chat code of lanes CT and CS, for Lars to review. |
| HT3 | Built by lane HT (2026-10-25): **a bound that the test owns is set from what the test proves, not from its other waits**: the running cancellation of `NhBackgroundOperationProviderTests` runs with options of its own whose lock timeout is the library's default of 5 s and waits with 30 s bounds instead of 2 s and 10 s, because its 100 ms heartbeats and the owner's request met at the operation lock and one gave up after the 250 ms that the deliberate contention checks keep; and the handlers of `NhBackgroundOperationReservedConcurrencyProviderTests` hold their slot for up to 5 minutes instead of the test's 30 s, because a holder that gave up under load freed its slot for the operation that must find none, for Lars to review. |
| HT4 | Built by lane HT (2026-10-25): **SPM-263 reads the entry of the conversation it created from the owner's list instead of assuming the owner has one conversation**; the class fixture shares the host and the owner, and xUnit v3 runs the class's tests in any order, so SPM-312's conversation was listed too. SPM-312 already filters by its own conversation and counts only the inbox requests after its start, and SPM-264 reads no list, so they stay as they are, for Lars to review. |
| HT5 | Built by lane HT (2026-10-25): **`test:affected` passes `--logger "console;verbosity=minimal"` to every `dotnet test`, shows each failed .NET test with its message and stack in the job block and names the failed tests under the process in the summary**; with `-v q` alone the matrix printed only the names of the failed SQL Server tests, so their causes could not be read. Parallelism and the schedule are unchanged, because the causes found were in code, so the matrix keeps its runtime, for Lars to review. |
| HT6 | Built by lane HT (2026-10-25): **no sample case or guidance rule for HT1 and HT2**: both are internal behavior without a public surface, as the earlier background-operation gate fix. The evidence is focused tests on both providers that make the interleaving load produced happen on every run, each failing on the old code: `NhBackgroundOperationAttemptWriteGateTests`, `Own_writes_go_first_after_a_beat_while_every_heartbeat_takes_longer_than_the_interval` (an interceptor holds every heartbeat for three intervals) and `A_reservation_books_against_the_ledger_another_reservation_created_after_its_increment` (an interceptor creates the ledger right after the first empty increment), for Lars to review. |
| HT7 | Built by lane HT (2026-10-25): **the commands that prepare test databases run with `RelationalTestServer.PreparationCommandTimeout` of 5 minutes**: `CREATE DATABASE` on both providers, emptying a pooled slot and migrating an assistant slot. In a load run all six SQL Server processes failed one test each at the same moment on `CREATE DATABASE` with SqlClient's 30 s timeout, among them `Another_holder_of_the_lock_...` and a step-recording test, so whichever test created a database during a stall of the server failed. The commands of the code under test and the AspNet.Common tests' `EnsureCreated` keep their 30 s, for Lars to review. |
| HT8 | Built by lane HT (2026-10-25): **left as they are**: `A_cancellation_whose_worker_still_sends_heartbeats_...`, `DueTimesFollowTheApplicationClock...`, `The_steps_own_exception_survives_...(Root, Error)` and the five AI.Chat tests of train 22 never failed in the load runs with their own cause, no timing assumption was found in them, and a check of pooled assistant schemas found no writes after a lease was returned; each creates or leases a database first, so HT7 is their likely cause, which HT5 will show if not. Under load beyond the matrix's (six to seven times slower), MCP tests also failed in one run, most likely on the library's 10 s `Mcp.ConnectTimeout` against their local servers; they stay as they are, for Lars to review. |
