# Application Context for Agents Plan

This document is the architecture and implementation plan for what agents know about the application
they work in: a generated map of the data they may read, the gateway's read tools in headless agent
runs, and retrieved context in agent turns. It complements the [agent roles
roadmap](ai-agent-roles-plan.md): a [subject briefing](ai-agent-briefing-plan.md) describes one
subject, this plan describes the application around it. A vector index over application text is a
later plan; this one prepares its place. It was checked against `feature/ai-agent-roles` at
`7824e8d3` on 2026-10-15.

## Status

Part 1 (application map, SPM-289) is implemented; parts 2 and 3 are not yet. The choices are part
of the [decision record](#decision-record) and the [answered questions](#answered-questions); the
implementation deviates from the plan where [Implementation notes](#implementation-notes) says so.

## Implementation notes

- **Opt-in.** `ConfigureAgent` makes an agent run on its own and requires background operations,
  so the map, a feature of every turn, is enabled with `ConfigureAgentContext(agentId, context =>
  context.UseApplicationMap())` instead. It applies to chat agents, consultations and runs alike.
- **Which turns list a resource.** Entries carry the tools that read them
  (`NhAiApplicationMapEntry.ToolIds`); the gateway names its `query` and `get` tools. A turn lists
  an entry when it offers one of its tools, and an entry without tools in every turn. The map is
  added once the turn's tools are selected, read once per turn, and recomposed when a skill
  changes the tools.
- **Telemetry.** The map's hash reaches the business audit as `ApplicationMapHash` on tool events,
  next to the other prompt identities.
- **Relations.** The convention reads the filter fields of a resource's `query`. `IgnoreRelation`
  is checked like `Relate`: both fail at startup on an unknown resource or filter field. Without
  published resources, such as with the bridge disabled, nothing is checked.

## Problem

- **The model learns the application by exploring it.** A turn receives the library rules, the
  application context that administrators write (free text, at most 20,000 characters), the agent's
  instructions and the turn data. Which data exists and how it relates is in none of them, unless an
  administrator wrote it down and keeps it current with every release. In a person's turn the
  gateway's `search-resources` answers it, but only when the model asks: one more model call per
  turn, with the instructions, tools and history again.
- **Headless runs have no generic reads.** Bridge and MCP tools are not offered in headless runs,
  because they forward the caller's bearer token and depend on the request (see the non-goals of the
  [autonomous agent runs plan](ai-autonomous-agent-runs-plan.md#non-goals)). A run sees its subject
  through the briefing (A2) and everything else through curated tools that each application writes
  per agent, or not at all.
- **Retrieval is built but not connected.** `INhAiContextResolver` authorizes per source, filters on
  execution scope and classification, resolves conflicts, ranks, applies budgets and writes a
  content-free trace. No assistant turn calls it; only the sample's `ProjectAiContextSource` and the
  tests use it.

## Goals

- An agent that reads application data starts every turn with a compact, generated map of what the
  acting person may read, so the model knows where to look without searching first.
- Headless runs read application data through the same gateway, under the same controller
  authorization, as people's turns.
- Registered context sources reach agent turns as bounded, untrusted data with a trace.

## Non-goals

- Embeddings, a vector store or indexing on data changes. The ingestion pipeline exists; feeding it
  from application events and searching it is a later plan, delivered as one more context source
  (see [later: a vector index](#later-a-vector-index)).
- Write access through the bridge in headless runs. Changes stay curated tools with proposals.
- Individual bridge tools and imported MCP tools in headless runs; only the four gateway reads.
- Rights beyond those of the person the agent works for. Agent grants do not apply to gateway reads.
- A retrieval tool that the model calls with its own query; the retrieved block is automatic.
- Schema changes. Nothing in this plan stores new data.

## Design

### Part 1: application map

A generated description of the readable application, offered to the model as data.

```csharp
// NewHeap.Platform.AI.Common
public interface INhAiApplicationMapSource
{
    ValueTask<IReadOnlyList<NhAiApplicationMapEntry>> GetEntriesAsync(
        NhAiInvocationContext context,
        CancellationToken cancellationToken);
}

public sealed record NhAiApplicationMapEntry(
    string Resource,                         // dash-case, as the gateway names it
    string Title,
    string Summary,
    IReadOnlyList<string> Operations)        // query, get
{
    public IReadOnlyList<NhAiApplicationMapRelation> Relations { get; init; } = [];
}

public sealed record NhAiApplicationMapRelation(string Field, string Resource);
```

- **Source.** The gateway implements the source from the resources the acting person may use: the
  list `search-resources` filters today (`AvailableResourcesAsync`), with the localized titles and
  summaries of `UseLocalizedResourcePresentation`. Applications may add sources with
  `AddApplicationMapSource<T>()`. The contract lives in AI.Common, so AI.Chat does not depend on the
  bridge.
- **Relations.** By convention, a filter field `<resource>Id` of one resource that names another
  resource is a relation: `project-task.projectId → project`. On the gateway,
  `Relate("project-task", "projectId", "project")` adds one the convention misses and
  `IgnoreRelation("project-task", "projectId")` removes a wrong one. Startup fails on an explicit
  relation to an unknown resource or filter field, and logs the number of derived relations.
- **Where it applies.** Agents opt in with `ConfigureAgent(id, agent => agent.UseApplicationMap())`;
  no agent gets the map by default, and the sample and the guide turn it on. The map lists gateway
  resources only in turns that offer the gateway's `query` or `get` tool to the agent, so it never
  points at data the turn cannot read. That covers people's turns today and headless runs once part
  2 exists.
- **How the model sees it.** An `# Application map` section with an `<application-map>` block,
  introduced as data supplied by the application, not instructions. One line per resource, for
  example `- project-task (Tasks): Tasks of authorized projects. Operations: query, get. Relations:
  projectId → project.` A closing line says that `describe-resource` gives the filter, order and
  result fields.
- **Position.** Directly after the instructions, before the situation data. The instructions,
  preferences and map change only with a release, a permission or a preference, so providers with
  prefix caching reuse them across turns; the situation data after them changes every turn. Like the
  other turn data, the map is not part of `PromptHash` or the approval binding. Its content hash
  goes to the turn's telemetry, so evaluation (D) can compare runs.
- **Bounds.** `NhAssistantLimits.MaxApplicationMapCharacters`, 4,000 by default and at most 16,000.
  Entries are kept in source order (gateway resources sorted by id) until the bound is reached; the
  rest are left out with a note to use `search-resources`.
- **Failures.** A source that throws or takes longer than `ApplicationMapTimeout` (2 seconds by
  default) leaves the turn without the map; the log names the exception type only.
- **The application context stays.** The administrators' text explains the domain: its terms,
  processes and priorities. The guide tells them to stop listing entities there; the map does that
  and stays current by construction.

### Part 2: gateway reads in headless runs

- **Scope.** Only the four gateway tools: `search-resources`, `describe-resource`, `query` and
  `get`. The gateway never reaches mutations or reads that require approval, so a headless run gains
  no way to change data.
- **Opt-in with two keys.** The agent's tool selectors select the gateway tools, and its options
  allow them with `ConfigureAgent(id, agent => agent.AllowGatewayReads())`. Without both, headless
  runs behave as today.
- **Whose rights.** Those of the run's accountable person, the person the agent works for. The
  bridge stays the authorization boundary: each call runs through the controller with that person's
  policies, model binding, filters and trusted query bindings.
- **Division.** The run's invocation context carries the division that the subject's
  `INhAiSubjectAccessResolver.DescribeAsync` returns as its division scope, and trusted query
  bindings put it into the call, as they do with the request's division in a person's turn. The
  bridge forwards no active-division header in either kind of turn.
- **Discovery.** Today the bridge's discovery binds the invocation actor, or the agent's accountable
  owner, to the current HTTP principal. In a headless run it binds to the principal of the read
  credential instead, so `search-resources`, `describe-resource` and the map show exactly what the
  calls can read.
- **Shielding.** Gateway results in a subject thread follow the rule for results of agent grants:
  they reach only thread readers who satisfy the underlying bridge action's policies; others see
  `restricted`.
- **Failures.** A credential that cannot be issued, has expired or is rejected becomes
  `api-bridge-unauthenticated` or `api-bridge-forbidden` for that call. The run continues, as with
  any failed tool call.

#### Read credential

The self-HTTP call needs a credential of the accountable person while that person is not present: a
short-lived JWT, validated by the host's own bearer scheme.

```csharp
// Hosts on NewHeap identity: the built-in issuer
builder.Services.AddNewHeapPlatformAIDelegatedReads();

// Other hosts: their own issuer
builder.Services.AddNewHeapPlatformAIDelegatedReads(reads => reads
    .UseIssuer<ExternalIdentityReadIssuer>()
    .Lifetime(TimeSpan.FromSeconds(60)));
```

```csharp
// NewHeap.Platform.AI.AspNet.Common
public interface INhAiDelegatedReadCredentialIssuer
{
    ValueTask<TaskResult<NhAiDelegatedReadCredential>> IssueAsync(
        NhAiDelegatedReadRequest request,
        CancellationToken cancellationToken);
}

public sealed record NhAiDelegatedReadRequest(
    string PersonActorId,
    string? DivisionId,              // for hosts that carry the division in the token
    Guid InvocationId,
    TimeSpan Lifetime,
    IReadOnlyList<Claim> Claims);    // the marker claim the token must carry

public sealed record NhAiDelegatedReadCredential(string BearerToken, DateTimeOffset ExpiresAt);
```

- **Built-in issuer.** Without `UseIssuer`, the registration creates the token with
  `INhAuthenticationService.CreateToken(userId, claims, lifetime)`, so it carries the same claims
  and security stamp as the person's own tokens and the existing session validation rejects a
  removed person. `NewHeap.Platform.AI.AspNet.Common` already references
  `NewHeap.Platform.AspNet.Common`, so this adds no dependency. Startup fails when an agent allows
  gateway reads and no issuer is available.
- **Restriction.** Every token carries the claim `nh-ai-delegated-read` with the invocation id. The
  registration post-configures the host's JWT bearer scheme and, composed with its existing events,
  checks that claim in `OnTokenValidated`: the request must be a `GET` to an action that a gateway
  resource exposes, with the matching `X-NewHeap-AI-Invocation` header. Every other request with the
  token fails authentication, including the assistant's own endpoints, `/mcp` and minimal APIs, and
  so does a request whose endpoint is not yet known at authentication time. The restriction does not
  depend on each controller.
- **Principal for discovery.** The library authenticates the credential through the host's own
  scheme, including claims transformation, on a library-internal request context that the
  restriction recognizes; an external request cannot produce that context. Discovery and the call
  therefore see the same principal.
- **Lifetime.** Issued per tool call, 60 seconds by default and at most 5 minutes, and held only for
  that call. Like the caller's own token today, it never enters the invocation context, audit
  records or logs.
- **Instances.** The token is stateless, so it works when the self-HTTP call reaches another
  instance of the application; no shared cache is needed.

### Part 3: retrieved context in turns

- **Opt-in per agent.** `ConfigureAgent(id, agent => agent.UseRetrievedContext(context => context
  .Sources("project-documents").MaxItems(8).MaxCharacters(6_000)))`. Sources are registered with
  `AddContextSource<T>()` in AI.Common; an unknown source id fails startup. Agents without it behave
  as today.
- **Request.** Once per turn, before the first model call; tool rounds reuse the result. The query
  is the person's latest message in a chat turn, the question in a consultation, and the trigger
  messages of the run's inputs plus the subject's display name in a headless run, at most 1,000
  characters. `MaximumClassification` is the highest classification the agent's model profile
  permits.
- **Authorization.** Unchanged: the resolver asks `INhAiContextAuthorizationPolicy` per source
  before it reads, and drops items outside the invocation's execution scopes. A headless run passes
  the run's invocation context, so sources see the agent actor and its accountable person.
- **How the model sees it.** A `# Retrieved context` section, serialized with
  `INhAiContextFormatter.FormatAsData`: per item the source, item id, effective date and provenance
  references, and the content, with markup characters neutralized like the other data blocks. It is
  introduced as untrusted data that may be outdated or wrong, never as instructions, and is the last
  of the turn data because it changes with every question.
- **Bounds.** The agent's limits, within `NhAssistantLimits.MaxRetrievedCharacters` (6,000 by
  default, at most 24,000), become the request's `MaxItems`, `MaxCharacters` and
  `MaxEstimatedTokens`, so the resolver's ranking decides what fits. The block counts against the
  turn's history budget, like the briefing.
- **Trace.** The library registers an `INhAiContextTraceSink` that writes the content-free
  resolution trace (sources, outcome codes, counts and context hash) to the turn's telemetry with
  the invocation id.
- **Failures.** A resolver that throws or takes longer than `RetrievalTimeout` (5 seconds by
  default) leaves the turn without retrieved context; the log names the exception type only.
- **No retrieval tool yet.** The model cannot retrieve again with its own query during a run. For
  structured data the gateway's `query` already searches; a retrieval tool becomes worthwhile with
  the vector index and is decided then, with evidence from evaluation (D).
- **Next to briefings.** The briefing (A2) is what the application decides matters about the
  subject; retrieved context is what matches the current question. An agent can use both.

### Prompt layout

The instructions and turn data of one turn, after this plan:

| Order | Block | Changes when | In `PromptHash` |
|---|---|---|---|
| 1 | Library rules | the library is upgraded | yes |
| 2 | Application context | administrators edit it | yes |
| 3 | Agent instructions | the agent's version changes | yes |
| 4 | User preferences | the person changes them | yes |
| 5 | Application map (new) | a release or the person's permissions change | no |
| 6 | Situation | every turn | no |
| 7 | User's screen | every turn | no |
| 8 | Retrieved context (new) | every turn | no |

The subject briefing (A2) and the continuity block keep the places their plans give them.

## Decision record

Proposed on 2026-10-15; decided the same day.

| # | Decision | Rationale |
|---|---|---|
| D1 | Structured data is queried live through the gateway; rows are not embedded. | Exact, current and authorized by the controller; nothing to keep in sync. |
| D2 | The map is generated per person from the gateway's resources, not written. | Current by construction; lists only what the person may read. |
| D3 | The map is opt-in per agent and appears only in turns that offer the gateway's `query` or `get` tool. | Existing agents keep their prompt and cost after an upgrade; the map never points at data the turn cannot read. |
| D4 | Relations follow the `<resource>Id` convention, with explicit `Relate` and `IgnoreRelation`. | Most relations come for free; the exceptions stay in the application's hands. |
| D5 | Map and retrieved context are turn data outside `PromptHash` and the approval binding. | A release or another question never invalidates a pending approval. |
| D6 | Headless runs get the four gateway reads only, with the accountable person's rights, enabled with two keys. | Generic reads without new rights or a write path. |
| D7 | The read credential is a short-lived JWT of the host's own bearer scheme, issued per call, restricted by the library to gateway `GET` calls. | Stateless across instances, reuses the host's validation and session checks, and the restriction does not depend on each controller. |
| D8 | The library ships an issuer for hosts on `INhAuthenticationService`; other hosts supply `INhAiDelegatedReadCredentialIssuer`. | NewHeap applications need no custom token code; others keep token creation in their own hands. |
| D9 | Retrieval runs once per turn through the existing resolver, opt-in per agent, without a retrieval tool. | The resolver already has authorization, scopes, ranking, budgets and a trace; a tool waits for the vector index. |
| D10 | A failing map, credential or retrieval never fails the turn. | They are aids, like the briefing. |

## Delivery phases

1. **Application map** (SPM-289). The contracts in AI.Common, the gateway source and relations in
   AI.AspNet.Mvc, and the block, limits and opt-in in AI.Chat. The sample's project assistant
   answers "which tasks of this project are overdue" with one `query` call instead of searching
   resources first.
2. **Headless gateway reads** (SPM-290). The issuer contract, the built-in issuer and the
   restriction in AI.AspNet.Common, headless discovery in AI.AspNet.Mvc, and the opt-in, division
   scope and shielding in AI.Chat. The sample's project planner reads a project's tasks through the
   gateway in a headless run with the built-in issuer, and its credential fails on a `POST` and on
   an endpoint outside the gateway. A security review precedes the release.
3. **Retrieved context** (SPM-291). The opt-in, query, block, limits and trace sink in AI.Chat. The
   sample's `ProjectAiContextSource` feeds the project assistant; items outside the division scope
   do not reach it.

Phase 3 depends on neither 1 nor 2 and can be built alongside them; phase 2 makes the map of phase 1
available in headless runs. Each phase is a minor release of nuget-common with its guidance rules
(`nh-ai-api-bridge` for phases 1 and 2, `nh-ai-agent-runs` for 2 and 3, `nh-ai-context-retrieval`
for 3), the consumer guide `ai-agent-runs` and release notes.

## Verification

No phase changes the schema. Phases 2 and 3 involve run persistence, so their end-to-end tests run
on SQL Server and PostgreSQL.

- **Map:** it lists exactly the resources the person may use, in order and within its bound, with
  derived, added and ignored relations; it is absent for agents without `UseApplicationMap` and in
  turns without a gateway read; a throwing source leaves the turn without it; a release that adds a
  resource changes the map hash and not the prompt hash.
- **Headless reads:** a run with both keys reads through the gateway with the accountable person's
  rights and the subject's division, with the built-in issuer and with a custom one; without either
  key the tools are not offered; the credential fails on `POST`, on an action outside the gateway,
  on the assistant endpoints, without or with another invocation header and after it expired; a
  removed person gets `api-bridge-unauthenticated`; thread readers without the action's policy see
  `restricted`.
- **Retrieved context:** retrieved once per turn, within its bounds, after the situation data and
  marked untrusted; items outside the invocation's scopes or above the classification are dropped; a
  slow resolver leaves the turn without it; the trace holds no content.

## Risks

- **Credential misuse** is the largest new surface: a read token for a person, issued while that
  person is not present. The restriction (D7), the lifetime and the security review exist for it.
- **Leaking through the model.** In a shared subject thread the model may restate what it read with
  the accountable person's rights in a summary or proposal that other readers see. Shielding covers
  tool results, not the model's own text. As for agent grants, the guide asks applications to keep
  what a subject's readers may see in line with what its accountable person may read.
- **Prompt injection through retrieved text.** Retrieved items are untrusted and marked so, and the
  library rules already place tool and document content below every instruction.
- **Cost.** The map adds up to its bound to every model call. Prefix caching makes repeated calls
  cheap where providers support it, and the default bound is small.

## Answered questions

Answered on 2026-10-15:

- **Is the map on by default for agents that select the gateway tools?** No; it is opt-in for every
  agent, and the sample and the guide turn it on.
- **Which credential do headless gateway reads use?** A short-lived JWT validated by the host's own
  bearer scheme, with the library's restriction in `OnTokenValidated`, rather than an opaque handle
  that would need a shared cache across instances and policies that accept an extra scheme.
- **Does the library ship an issuer?** Yes, for hosts on `INhAuthenticationService`; other hosts
  implement `INhAiDelegatedReadCredentialIssuer`.
- **How do relations arise?** By the `<resource>Id` convention, with explicit `Relate` and
  `IgnoreRelation` for the exceptions.
- **Do agents get a tool to retrieve again with their own query?** Not now; the retrieved block is
  automatic, and a tool is decided with the vector index.

## Later: a vector index

A vector index over application text, such as descriptions, comments, documents and earlier run
summaries, becomes one more `INhAiContextSource`. The existing ingestion pipeline fills it from the
events that already start agent triggers after the commit. Its items carry ids, scopes and
classification, and the model reads the live record through the gateway's `get` before relying on
one: retrieve ids, read live. Whether the model also gets a retrieval tool is decided then.
