# Agent Runs Adoption Plan

This document records library work that a consuming application asked for while adopting the agent runs
of `NewHeap.Platform.AI.Chat` on NuGet 19.0.0 and npm 0.18.0: an application with external identity
(OIDC, rights from a claims transformation per request), one tenant per host, no agents of its own next to
NewHeap's, and its own approval channel. It is a plan, not usage documentation.

Rules for all of it: every item is opt-in, so existing applications keep today's behavior; the library
stays generic; NewHeap stores and enforces only, and consumer data never grants rights; each item has
code and migrations for SQL Server and PostgreSQL, tests with authorization negatives, a release-notes
section, consumer guide and guidance updates, an executable sample case, and Angular parts with English
and Dutch texts.

## Status

Decided by Lars on 2026-10-23, every choice as recommended except S11 (b). Nothing is built yet.
Delivery: train 17 builds items 1 to 4 and S10, train 18 S5, S6 and S11, train 19 S8, S9 and S7-1, train
20 S7-2.

## Verified on 19.0.0

- `NhAssistantSystemApprovers.InboxAsync` returns nothing to a caller with a tenant, and boards do the
  same, so a host whose resolver sets a tenant on every person cannot see or decide whole-application work.
- Agents have no place for an application's own fields.
- `NhAssistantProposalDecisions` and `NhAssistantApproverCheck` are internal; deciding works only through
  the endpoints in the approver's own request.
- `AgentRunEnded` carries run, agent and version, trigger id, skill and status with failure code, but not
  shadow, the kind of trigger, the skill version or the number of model calls;
  `NhAiUsageRecord.InvocationId` identifies the invocation context, not one model call.
- No guidance router links a generated `references/ai-*.md`, and no validator requires it.
- Imported MCP tools next to an MCP export on one host already work: imports never carry MCP exposure, the
  export skips them, and tests and guidance say so.

## Items to build (train 17)

### 1. Single-tenant mode for whole-application work

| # | Decision |
|---|---|
| ST1 | **An explicit opt-in.** `UseSingleTenant(tenantId)` on the assistant builder; startup fails when the AI ASP.NET integration projects another tenant or more than one. Not derived from `UseAuthenticatedClaimsForSingleTenant`. |
| ST2 | **Only who may see and decide changes.** Whole-application (`nh-system`) threads, runs and proposals stay stored without a tenant. In the mode a caller whose tenant equals the configured one is eligible for whole-application work (inbox, decisions, boards, live watches, schedules without subject sets, runs started by hand), a caller with any other tenant sees nothing, and `UseSystemSubject`'s policies and the agent's run and view policies still decide. No migration; switching the mode off leaves the data valid. |
| ST3 | Built by lane ST (2026-10-23): **one internal rule, `NhAssistantWholeApplicationTenancy`**: `TakesPart(tenant)` (no tenant, or the configured one), `Reaches(stored tenant, subject type, tenant)` (the same tenant, or a tenantless `nh-system` record for a caller who takes part) and `KeepsOut(tenant)` (only with the mode: another tenant); every check calls it, and the conversation store applies `Reaches` to tenantless whole-application threads, so the thread's conversation, messages, turns and decisions in the conversation follow without changes at their call sites; for Lars to review. |
| ST4 | Built by lane ST (2026-10-23): **without the mode nothing changes, also where no tenant was compared before** (deciding by id, the decision service, `POST threads/start` and `threads/open`, `GET threads` and the thread's runs, proposals, memory and files, `GET subjects/nh-system/.../work`, `/agents` and `/summary`): there the mode keeps a caller of another tenant out (`KeepsOut`), while a host without the mode keeps today's behavior, in which a caller with a tenant may still decide a whole-application proposal by its id or start an agent for it although the inbox, boards and the thread's conversation hide it. Closing that gap for every host is a separate breaking change; for Lars to review. |
| ST5 | Built by lane ST (2026-10-23): **a caller without a tenant stays eligible in the mode**, as before: an issuer projected with `UseAuthenticatedClaimsWithoutTenant`, background work and an application channel that passes no tenant to the decision service; for Lars to review. |
| ST6 | Built by lane ST (2026-10-23): **the startup check reads the AI ASP.NET projections**: it fails for an issuer with a tenant claim (`UseAuthenticatedClaims`, `NhAiAspNetIssuerClaimMapping`) and for another fixed tenant (`UseAuthenticatedClaimsForSingleTenant`), naming the issuer and the remedy; an issuer without a tenant passes; a resolver of the application's own (`UseAuthenticatedInvocationContextResolver`) is documented, not checked or logged; for Lars to review. |
| ST7 | Built by lane ST (2026-10-23): **what the mode keeps from another tenant reads as unknown**: `404` with the existing codes (`assistant-approval-not-found`, `assistant-conversation-not-found`, `assistant-subject-not-found`, `assistant-board-not-found`), an empty inbox, watch, history and search, and zero counts in the subject summary, as for other work a person may not see; for Lars to review. |
| ST8 | Built by lane ST (2026-10-23): **only the whole application opens up**: the application's own subjects keep the tenant their resolver gives (`NhAiSubjectInfo.TenantId`), and the store relaxes its tenant comparison only for a conversation of kind thread whose thread is `nh-system` without a tenant; for Lars to review. |
| ST9 | Built by lane ST (2026-10-23): **the caller's tenant reaches the shared decision as an optional last parameter** of `NhAssistantProposalDecisions.DecideAsync` (both overloads), `ResolveExecutionAsync` and `FactsAsync`, so lane DA's implementation stays one; the endpoints pass the caller's tenant and `INhAssistantProposalDecisionService` passes `NhAssistantDecidingPerson.TenantId` for decisions and facts; for Lars to review. |
| ST10 | Built by lane ST (2026-10-23): **the run history finds the whole application's runs in the same query**: `FindThreadsWithRunsAsync` adds tenantless `nh-system` threads for a caller of the single tenant (provider-neutral LINQ, both providers tested, no migration), and `GET runs/{id}` uses `Reaches`; for Lars to review. |
| ST11 | Built by lane ST (2026-10-23): **schedules without subject sets need no change**: they start runs in the whole application's threads, which the rule opens to the tenant's people; hand-off links name a source thread only, opening it checks the tenant; for Lars to review. |
| ST12 | Built by lane ST (2026-10-23): **the sample extends SPM-302 instead of a new case**: `SampleSingleTenantComposition` and a focused host (`AssistantSingleTenantSamplesTests`) whose people sign in at a customer's identity provider, with a principal factory of the application's own for `oidc:` actor ids and an agent created in the administration, show the board and the thread to the tenant's people; the public `INhAssistantSubjectWork.GetAsync`/`SummarizeAsync` take no tenant (the application vouches) while their request extensions apply the mode; for Lars to review. |

### 2. Extensible agent metadata

| # | Decision |
|---|---|
| MD1 | **Named, typed entries registered in code.** `AddAgentMetadata<T>(name, ...)` with a JSON shape, a size limit and a validator that runs on save as startup validates code; values stored per agent and name in a table of their own (migration for both providers), for agents in code (set in `ConfigureAgent`, read-only unless the entry is editable) and agents created at runtime. Nothing in NewHeap interprets them, and they never widen rights. Removed with the agent and carried by export and import (S7-2). |
| MD2 | **Lookup by a declared index.** An entry declares its searchable value (`IndexBy(...)`), stored in an indexed column on save; `ListAgentsAsync(name, value)` queries it with provider-neutral LINQ. |
| MD3 | **Saved per entry.** `PUT admin/agents/{id}/metadata/{name}`, also through `INhAssistantAgentConfiguration` and `CreateAgentAsync`, with the expected version: an entry of an agent created at runtime makes the agent's next version, an editable entry of an agent in code is an override with a version of its own. The audit names the entries, never their content. The agent editor has a slot per entry where the host renders its own editor, which saves itself; no generic form engine. |

### 3. A public decision service for application channels

| # | Decision |
|---|---|
| DA1 | **The application vouches for the person.** A public `INhAssistantProposalDecisionService` with `GetDecisionFactsAsync(person, approvalId)` (proposal hash, expiry, whether four-eyes applies, approvals still needed, whether this person may decide; no proposal content) and `DecideAsync(person, approvalId, expectedHash, decision, channel, idempotencyKey)`, where the person is the `ClaimsPrincipal` and actor id the application authenticated. It applies exactly the endpoint's checks (approver, four-eyes, hash and staleness, `RequireUnchangedSubject`, expiry, duplicates, cooldown, follow-up run) through one shared implementation, and records the channel in the content-free audit. The guidance says it is a server API whose caller vouches for the authentication. |
| DA2 | **Idempotent by state.** A repeat by the same person with the same decision and hash returns the earlier decision as a duplicate; anything else gets the ordinary refusal. The key is recorded only in the audit; no migration. |
| DA3 | Built by lane DA (2026-10-23): **the service decides exactly what the endpoint decides**: proposals of agent runs (deferred, and inline while their run waits) and deferred proposals a consultation left in the consulted agent's thread; a person's own inline chat proposal and an inline consultation proposal the asker decides are refused with the endpoint's codes (`assistant-approval-decision-invalid`, `assistant-consult-asker-decides`), because they resume a streamed turn in the person's conversation; for Lars to review. |
| DA4 | Built by lane DA (2026-10-23): **the shared implementation is `NhAssistantProposalDecisions.DecideAsync` itself**: the endpoints call it unchanged (an overload passes the `web` origin), the service passes its own channel and key, and only the audit event gained `Channel` and `IdempotencyKey`; the checks were not moved or rewritten. The facts make the same checks in the same order without effects (`FactsAsync`: an expired proposal is not closed and the proposal guards do not run) and follow the decision's visibility: not found where a decision is, a refusal code (such as `assistant-approval-policy-denied` for someone outside the project) where a decision would be refused. `ApprovalsNeeded` is 1 while the proposal is pending before its expiry, otherwise 0; one approver decides, four eyes only says who; for Lars to review. |
| DA5 | Built by lane DA (2026-10-23): **duplicates through the service only, read from the proposal's state**: when the decision answers `assistant-approval-not-pending`, also for two deliveries at the same moment, the service returns the earlier decision if the proposal names the same decider and hash and its status still says that decision (approved, executing, executed, execution-failed or execution-unknown for an approval; rejected for a rejection), also when that decision came from the endpoint; stale, withdrawn, expired and resolved proposals keep the refusal. A duplicate is not audited again. The endpoints keep answering a repeat with `409 assistant-approval-not-pending`, so existing clients see no change; for Lars to review. |
| DA6 | Built by lane DA (2026-10-23): **programmer input throws, channel data is a result**: an invalid channel, a blank expected hash, an undefined decision or a person without principal or actor id throw `ArgumentException` (the application chooses them in code); an idempotency key that is not usable fails with `assistant-approval-decision-invalid` (it comes from the channel's delivery). The decision is an enum (`NhAssistantProposalDecision`); no new error code; for Lars to review. |
| DA7 | Built by lane DA (2026-10-23): **channel and key formats**: a channel is a dash-case code of at most 50 characters (`NhAssistantDecisionChannels.IsValid`), `web` is not reserved, so an application's own pages may use it; the key is at most 100 printable ASCII characters without white space (`IsValidIdempotencyKey`), so no free text reaches the audit. Only `ApprovalApproved` and `ApprovalRejected` of agent proposals carry them; decisions in a conversation, executions and resolutions keep `Channel` null; for Lars to review. |
| DA8 | Built by lane DA (2026-10-23): **the person carries an optional tenant**: `NhAssistantDecidingPerson(User, ActorId, TenantId = null)`, as `NhAssistantConversationPerson` does, so the tenant eligibility of whole-application work (ST2) can apply to the service as to the endpoints. The decision checks no tenant today, at the endpoint or in the service; when the train merges ST's tenant check into the decision, the service must pass `person.TenantId` where the endpoint passes the caller's tenant; for Lars to review. |
| DA9 | Built by lane DA (2026-10-23): **no reason parameter**: `DecideAsync` follows DA1's signature and records no reason; why a person rejected stays with proposal feedback. An optional reason can be added without breaking callers when a channel needs one; for Lars to review. |
| DA10 | Built by lane DA (2026-10-23): **the sample's channel is a messaging reply in SPM-268**: `SampleApprovalReplyChannel` offers a proposal only after the facts say the person may decide, issues a Data Protection reply token bound to the proposal, the person and the hash that expires with the proposal, rebuilds the person from the user store (`MaySignInAsync`, the claims of their sign-in) and decides their reply at the anonymous `POST approval-replies`, whose only proof is the token; a production channel also verifies the provider's signature; for Lars to review. |

### 4. Run facts for content-free usage

| # | Decision |
|---|---|
| RF1 | **Fields on `AgentRunEnded`.** Shadow, the kind of trigger, the skill version, the number of model calls and the status apart from the failure code, all content-free; model calls are counted per run (a column, with a migration). Each model call gets a stable `ModelCallId` in its `NhAiUsageRecord`. An application counts runs, rule-only runs and shadow runs per agent and skill from its audit sinks alone. |
| RF2 | Built by lane RF (2026-10-23): (a), for Lars to review. **Nine trigger kinds** in the public `NhAssistantTriggerKinds`: `event`, `schedule` and `webhook` by the registered trigger's source (in code or as data), `manual` (`manual`), `message` (`queued-message`, a person's message that waited for the run), `request` (`agent-request`), `hand-off` (`agent-handoff`), `follow-up` (`proposal-decided`, `proposal-executed`, `agent-handoff-ended`) and `replay` for a run that replays another run, whatever its trigger. A consultation gets no kind: it is no run and keeps its own `AgentConsulted` event. Alternative (b): fold `message` into `manual` and `replay` into the replayed trigger's kind; not chosen, because a typed message and an evaluation replay are different work for usage counts. |
| RF3 | Built by lane RF (2026-10-23): (a), for Lars to review. **The kind is recorded on the run when it is created** (`AssistantRun.TriggerKind`, 16 characters, migration `RunTriggerKinds` for both providers), so a trigger defined as data that is changed or removed while its run waits does not change it; a run continued in the shadow thread keeps the kind of the run it continues, and a run created before the migration takes its trigger's kind as registered at its end (`null` when no longer registered). Alternative (b): derive it only when the run ends, without a column; not chosen for that reason. |
| RF4 | Built by lane RF (2026-10-23): (a), for Lars to review. **No `RunModelCalls` migration**: `AssistantRun.ModelCalls` exists since `AgentEvaluation` (2026-10-15) and holds the count; this item's only migration is `RunTriggerKinds` (RF3). Alternative (b): a second column for the new count; not chosen, because it would duplicate the existing one. |
| RF5 | Built by lane RF (2026-10-23): (a), for Lars to review. **A model call counts on the run when its budget is reserved**: the assistant's budget manager adds every model (non-tool) reservation of a run's turn to `AssistantRun.ModelCalls` in one update, triage and a turn's own retry after a transient provider error included, instead of adding the turn's count when it returns. A turn that throws, is cancelled or is retried keeps its calls; consultations (no run) and proposal executions add none. The evaluation and the run history read the same column, so their counts rise for runs with such turns (release note). Alternative (b): keep the count per turn result; not chosen, because calls of turns that end by an exception were lost. |
| RF6 | Built by lane RF (2026-10-23): (a), for Lars to review. **`RunStatus` and `RunFailureCode` as two fields**, while `ResultCode` keeps `status:code` unchanged for existing sinks. Alternative (b): change `ResultCode` to the status alone; not chosen, because it breaks sinks that parse it. |
| RF7 | Built by lane RF (2026-10-23): (a), for Lars to review. **`SkillVersion` is the version of a skill defined as data and `null` for a skill in code**, whose version is the agent's (`AgentVersion`); `Shadow` is the run's shadow flag, so a candidate version of a skill counts as a shadow run in that version, without a separate candidate field. Alternative (b): report the agent version for skills in code; not chosen, because the event carries it already. |
| RF8 | Built by lane RF (2026-10-23): (a), for Lars to review. **A rule-only run is a run with `ModelCallCount` 0**; the event gets no run kind. A run that ended before its first model call, such as a run refused while its agent is stopped, also counts 0; combine with `RunStatus` `completed` where that matters. Alternative (b): add the run's kind (`model`, `rule`, `rule-then-model`); not chosen, because the count answers the usage question and the kind is already in the run history. |
| RF9 | Built by lane RF (2026-10-23): (a), for Lars to review. **`ModelCallId` is a version 7 GUID per call of `INhAiChatExecutor`**: the executor's own retries of that call share it, and so do its budget reservation (new `NhAiBudgetRequest.ModelCallId`, `Guid.Empty` for tool and embedding reservations), its `ai.model.invoke` activity (`newheap.ai.model_call_id`) and its usage record; `InvocationId` keeps naming the invocation context. A run's model calls match its usage records by the run id in `CorrelationId`. Alternative (b): an id on the usage record only; not chosen, because an application's budget manager could not match its reservation to the usage. |
| RF10 | Built by lane RF (2026-10-23): (a), for Lars to review. **Only `AgentRunEnded` carries the run facts**; trigger events and the run's tokens and tool calls on the event stay as they are (the run history and the evaluation report them). The sample's `SampleAgentRunFacts` (SPM-304) counts runs, runs without a model call, shadow runs, failed runs, model calls and trigger kinds per agent and skill from the events. Alternative (b): add the kind to `AgentTriggerAccepted` and tokens to `AgentRunEnded` as well; not chosen, to keep the item to what was asked. |

### S10. The guidance routers link the AI references

Each router (`skills/newheap-*/SKILL.md`) links its `references/ai-*.md` with a short note on when to read
them, and `guidance:validate` fails when a generated reference is linked by no router of its module.

| # | Decision |
|---|---|
| GR1 | Built by lane GR (2026-10-23): **every generated reference, not only `ai-*`**: the routers now link all 30 references that were missing, the 20 AI ones and 10 others (backend collection processing, bulk upsert and logging; background operation suspension and modules without NewHeap Identity; database bulk upsert; frontend toasts and logging; runtime-configuration logging); hand-written references (`package-sources.md`, `platform-fit.md`) and the bundle's evidence catalog, which every reference links, stay outside the rule, for Lars to review. |
| GR2 | Built by lane GR (2026-10-23): **AI references under a heading of their own at the end of the module**: "AI assistants, agents and tools" (backend, 14, in build order), "AI work in the background" (4), "AI invocation context" (authentication), "AI assistant panel" (frontend) and "AI features" (testing), each with a "when …" note in the module's list or sentence style; other references join the module's existing list or sentence; a module names another module in plain text (backend to authentication for the invocation context), because links between modules break in the plugin layout, for Lars to review. |
| GR3 | Built by lane GR (2026-10-23): **the router is the module's canonical `SKILL.md`, and a link is a relative Markdown link**: `references/<name>.md`, also with `./` or an anchor; a mention in code text or an external URL does not count; the plugin copy stays covered by `guidance:check`. The check runs in `validateSkill` with helpers in `tools/guidance/lib.mjs` that the generator shares, and `guidance:validate:test` covers a sample router and the real backend router without its `ai-assets` link; against the old routers it reports the 30 missing links, for Lars to review. |
| GR4 | Built by lane GR (2026-10-23): **the bundle router and the module descriptions name the new areas** (bundle router: backend AI assistants, agents, tools and model profiles; frontend toasts and the assistant panel; authentication the AI invocation context; background operations and AI work in the background; testing AI evaluations; the descriptions of the seven changed modules likewise, plus bulk upserts for database and telemetry exporter settings for runtime configuration), so a task reaches the module that links its reference; `agents/openai.yaml` is unchanged, for Lars to review. |
| GR5 | Built by lane GR (2026-10-23): **the maintenance skill states the rule, and nothing else changes**: `skill-impact.md` says a new reference must be linked from its module's router; no release note (guidance and tooling only, no public API), every version unchanged and only the plugin's `skillContentHash` regenerated, for Lars to review. |

## Suggestions to build later

### S5. A deferred proposal executed by the application (train 18)

| # | Decision |
|---|---|
| S5-1 | **Asynchronous execution by the application.** A governed tool declares that the application executes it. On approval NewHeap hands the approved payload, the proposal hash, the decider and the proposal id to an `INhAssistantProposalExecutor` of the application, and the proposal waits in a new status until the application reports its outcome through a public API (for example `ReportExecutionAsync`); the report closes the proposal, is audited, notifies, and starts the follow-up run with the outcome. |
| S5-2 | **A report deadline per tool.** After it the run maintenance sets the proposal to `ExecutionUnknown` with its attention notification, and an approver resolves it through the existing resolve flow (hand it to the application again, or mark it resolved). |
| S5-3 | **Outcome, code and a short summary.** Completed becomes `Executed`, needs attention `ExecutionUnknown`, failed `ExecutionFailed`, each with a code; an optional summary of at most 2,000 characters reaches the follow-up run as untrusted information of the application; the audit records only the outcome and the code. |

### S6. Rights outside a request from the application's access source (train 18)

| # | Decision |
|---|---|
| S6-1 | **An access source with a freshness bound, nothing stored by NewHeap.** The application implements `INhAiAccessSource.GetAsync(actorId)`, returning claims and the time they were valid; `UsePrincipalsFromAccessSource<T>(o => o.MaxAge = ...)` registers a principal factory that returns no principal when the source returns nothing or the context is older than `MaxAge`. The application decides whether and where it keeps contexts. Guidance tells hosts whose rights come from a claims transformation to use it; a sample shows it. |

### S11. MCP error messages keep their format and arguments (train 18)

A failed result published through the MCP export also carries its message's format string and arguments
in `_meta` (`newheap.com/message-format`, `newheap.com/message-arguments`), so a client can translate it;
the text stays as today.

### S7-1. Instructions from the application's assets (train 19)

A data skill and an agent created in the administration may store a reference (asset id, version, hash)
instead of inline instructions; an `INhAiTextAssetSource` of the application supplies the text. The hash
is checked on load; a missing asset or a different hash is a problem that takes the skill out of effect.
Inline instructions stay possible.

### S8. Trigger versions and candidates, and start states (train 19)

| # | Decision |
|---|---|
| S8-1 | **Data triggers get versions and candidates like skills.** Append-only versions; a change can run as a shadow candidate next to the live version and is promoted or discarded; comparison and history in the administration; a migration. |
| S8-2 | **Agents may start in shadow, data consultations start off.** An agent created at runtime may start in shadow (`StartsInShadow`), effective once its run settings exist, also within the same request; a new data consultation starts off and is offered only after an administrator switches it on. |

### S9. Hand-offs and shadow variants as data (train 19)

Hand-offs as data within a ceiling in code (`UseHandOffsAsData`), and data shadow variants of an agent
that always run in shadow, with the administration, validators that mirror the startup checks, problems on
load and a migration.

### S7-2. Export and import with locked parts (train 20)

A versioned JSON format for an agent with its run settings, skills, triggers, consultations and metadata;
import goes through the same validators as saving. A package may lock parts, such as a skill's
instructions; the administration shows them read-only and a save refuses to change them (`locked`) until
an administrator unlinks the part. It gets a plan of its own before it is built.
