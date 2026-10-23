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

### 4. Run facts for content-free usage

| # | Decision |
|---|---|
| RF1 | **Fields on `AgentRunEnded`.** Shadow, the kind of trigger, the skill version, the number of model calls and the status apart from the failure code, all content-free; model calls are counted per run (a column, with a migration). Each model call gets a stable `ModelCallId` in its `NhAiUsageRecord`. An application counts runs, rule-only runs and shadow runs per agent and skill from its audit sinks alone. |

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
