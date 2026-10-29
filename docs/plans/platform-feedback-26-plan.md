# Platform Feedback on 26.0.0 Plan

A consuming application that links applications through MCP and runs assistant agents reported ten points on
NuGet 26.0.0: four on MCP between applications, four on the assistant and its agents and two on the database
read tool. Each finding was checked against `main` at aacea7ef (NuGet 26.0.0, `@newheap/platform-ai-chat`
0.26.0). Nine hold as reported. FB56 holds only for the assistant's outbound connections: the inbound
protected resource already accepts `*.localhost`. This plan records one choice per finding. It is a plan,
not usage documentation: once it is built, the guidance rules, the release notes and the sample cases are
authoritative.

## Status

Decided by Lars on 2026-10-29: FB56 to FB66, every one as advised (option (a)). FB56 was decided on its own,
and the rest with "everything as advised". The decisions are summarized below; the sections under "Choices"
keep the options that were weighed. Not built yet.

## Decisions

| # | Decision |
|---|---|
| FB56 | **One loopback-name rule** (`localhost`, `*.localhost`, loopback IP literals) in the inbound resource, the assistant's options and the outbound guard. Outbound http stays behind `AllowsLoopback`, and a `*.localhost` name is accepted only when every address it resolves to is a loopback address. |
| FB57 | **Named protected resources** through `UseProtectedResource(name, configure)`, each with its own scheme, policy, audience and metadata path. The current overload stays the default resource. |
| FB58 | **A public way to mark a token validated by the application's own scheme as never passed on.** |
| FB59 | **An optional application-provided source of chain callers and context bindings**, read per request with a short cache and validated per entry. |
| FB60 | **The composition catalog builds its model in a scope of its own**, with guidance that step tools do not depend on the request. |
| FB61 | **An opt-in follow-up turn in a person's chat after an execution report.** It runs with that person's rights and can only propose. |
| FB62 | **The store keeps the newest skill candidate** (compare-and-set), and an older version answers `newer-proposed`. |
| FB63 | **The three Agent Framework conflict switches on `NhAiAgentCreateRequest`**, with their defaults unchanged. |
| FB64 | **The MySQL flavor and version check on the handshake version, before anything that needs 8.0**, tested on a `mysql:5.7` container. |
| FB65 | **An additive profile value `mysql-compatible`** that detects the flavor and keeps each flavor's minimum. |
| FB66 | **Train 31 with lanes PR (FB56 to FB58), TB (FB59), CA (FB60, FB62), FU (FB61), AF (FB63) and MY (FB64, FB65).** TB merges after PR. |

## Rules for all of it

- Every change is opt-in or compatible unless its choice says otherwise. A breaking change gets a
  `Breaking change | Required action` row.
- Every change follows the repository rules:
  - code and migrations for SQL Server and PostgreSQL where persistence changes;
  - tests with authorization negatives;
  - a release-notes section per release unit;
  - guidance and consumer guide updates;
  - an executable sample case, extending the existing case where one covers the surface;
  - the public API snapshot for a public change.
- Nothing names the consuming application.

## Choices

Each choice lists the options that were weighed. "(advice)" marks the recommendation.

### A. MCP between applications

#### FB56. Plain http on a `.localhost` name

The inbound protected resource accepts plain http for a loopback address, `localhost` and any `*.localhost`
name (RFC 6761). It does so for its own resource URL and for every authorization server
(`NhAiMcpProtectedResource.IsLoopback`), and it reads an http issuer's keys over http. The assistant's
outbound guard (`NhAssistantMcpConnections.CheckUri` and `CheckOAuthUri`) counts only the literal
`localhost` and loopback IP addresses. It therefore refuses `http://auth.dev.localhost` for OAuth
discovery, the token endpoint and registration in every environment, and for the server URL while
`Mcp:RequireHttps` is on. The assistant's own OAuth client options already accept `*.localhost`, so the
library has three answers to one question.

- **(a) One loopback-name rule everywhere (advice).**
  - `localhost`, `*.localhost` and loopback IP literals count as loopback names in the inbound resource,
    the assistant's options and the outbound guard, through one shared helper.
  - Outbound http stays behind `AllowsLoopback`: always in Development, and with `Mcp:AllowLoopback`
    outside Production.
  - A `*.localhost` name is accepted over http only when every address it resolves to is a loopback
    address.
  - The release note says that the inbound side has accepted these names since 25.0.0, so a refusal
    there has another cause and the consumer should send its error code.
- **(b) A development switch with a list of http hosts.** This is more flexible, but the list can reach
  staging and send tokens in plain text.
- **(c) Only guidance:** https with a development certificate on `*.dev.localhost`, as the sample does.

#### FB57. More than one protected resource on a host

`UseProtectedResource` refuses a second call ("once per host"). A host has one authentication scheme, one
policy, one validator and one resource identifier, and it serves the root metadata address for that
resource. An application with an MCP entry for AI apps and another for other applications validates the
second one's tokens itself.

- **(a) Named protected resources (advice).**
  - `UseProtectedResource(name, configure)` gives each resource its own scheme, policy, audience,
    authorization servers, scopes and metadata at `/.well-known/oauth-protected-resource/<path>`.
  - The current overload stays as the default resource and keeps the root metadata address.
  - A test per resource proves that a token for one resource never signs in at another.
  - Tokens of every resource are marked as never passed on, as today.
- **(b) One resource with several resource identifiers.** This is smaller, but a token for one entry is
  then valid at the other: the audiences are no longer separated.
- **(c) Keep one resource,** and add guidance plus FB58 for an entry the application validates itself.

#### FB58. The "never pass on" marking for the application's own schemes

A token that the protected resource validated is marked as never passed on (the internal
`NhAiMcpProtectedResourceSignIn`). The caller-credentials accessor then hands it neither to the API bridge
nor to `forward-user-token` MCP servers. A token validated by the application's own scheme carries no
mark, so it is passed on. The application can neither set the mark nor decorate the internal accessor.

- **(a) A public way to mark the request's token as never passed on (advice).**
  - An additive call, for example `NhAiCallerCredentials.MarkNotForwardable(httpContext, issuer)`, made
    from the application's own `OnTokenValidated`.
  - The accessor reads the mark as it reads the built-in one.
  - With FB57 (a), a second protected resource is marked without this call.
- **(b) A configured list of schemes whose tokens are never passed on.** This is declarative, but policy
  schemes and forwarded schemes make the match uncertain.
- **(c) Make the default accessor public so that it can be decorated.** This adds more API, and an
  application can get the decoration wrong.

#### FB59. Chains and context bindings that change while the host runs

Reviewed chain callers (`ContinueMcpChain`, `UseMcpChainApplication`) and context bindings
(`AddMcpContextBinding`) are code registrations read into singletons at startup. A link between
applications created while the host runs counts only after a restart, and so does revoking one. The only
runtime lever is indirect: a binding stops when its administrator-connected server's id or URL changes.

- **(a) An application-provided source, read per request (advice).**
  - An optional interface returns the chain callers and context bindings the application reviewed and
    stored, next to the code registrations.
  - The answer is cached briefly, so that a link the application adds or revokes counts within that cache
    time.
  - NewHeap validates each entry as it validates a registration at startup, and leaves out an invalid
    entry with a logged reason.
  - The application owns who may create a link, as it owns approver candidates (FB50).
- **(b) Administrator data in NewHeap's own store,** with a permission and an audit trail. The trust
  decision then moves from code review to an administrator screen in NewHeap.
- **(c) Only revocation at runtime:** a disable list read per request. This only narrows; adding a link
  still needs a deployment.

### B. The assistant and its agents

#### FB60. Compositions and a scoped tool catalog

The composition tool catalog is a singleton. Its model reads every non-changing `INhAiToolCatalog`
from the root provider (`NhAssistantCompositionToolCatalog.Model.Create`). With scope validation on, the
default in Development, one scoped catalog stops the host at startup: "Cannot resolve scoped service from
root provider".

- **(a) Build the model in a scope of its own (advice).**
  - The catalogs are resolved inside `IServiceScopeFactory.CreateScope()`.
  - Guidance says that a composition step's tools must not depend on the request, and that a catalog
    whose descriptors do should implement `INhAiChangingToolCatalog`, which compositions leave out.
  - A test combines a composition with a scoped catalog and scope validation.
- **(b) A clear startup message, and guidance that composition catalogs are singletons.** This explains
  the limit but does not remove it.
- **(c) Build the model per call in a scoped holder.** This is larger, and the attestation hash must stay
  the same across scopes.

#### FB61. Continuing a person's chat after an execution report

When an application reports the execution of an approved effect (`ReportExecutionAsync`), the report is
posted to the conversation. In an agent thread, the approval policy's `StartFollowUpRun()` may start a
follow-up run as the initiator of the work, never as the decider. A person's own chat has no follow-up, so
the person picks the work up again after, for example, asynchronous publication steps.

- **(a) An opt-in follow-up turn in the chat (advice).**
  - A switch on the application execution builder, for example `ContinueChatAfterReport()`, starts one
    service turn after the report.
  - The turn runs with the rights of the person whose chat it is, never with the decider's, and with
    `RequireApprovalForEffects`: it can only propose, and a proposal waits for a decision like any other.
  - It is coalesced and skipped while the conversation is busy or waits for a decision.
  - An agent identity still cannot decide, and the report stays untrusted text for the model.
- **(b) A public observer after the report,** so that the application starts a turn itself. That turn
  would speak as the person through `INhAssistantConversationTurns`, without these limits.
- **(c) `StartFollowUpRun` for chats as well.** This needs a thread-like home for a chat's proposals,
  which makes it the largest option.

#### FB62. An older asset version replaces a newer skill candidate

`ProposeCandidatesAsync(assetId, version)` compares only with the live skill. It saves a candidate for any
newer-than-live asset version, and the store discards the existing candidate unconditionally. Proposing v2
after v3, with v1 live, therefore replaces the v3 candidate with v2. The check also runs before the save,
so two proposals at once can race.

- **(a) The store keeps the newest candidate (advice).**
  - The save discards only a candidate of an older asset version, as a compare-and-set inside its
    transaction.
  - Proposing an older version than the existing candidate answers a new outcome, `newer-proposed`, and
    saves nothing.
  - This also closes the race. The new outcome value gets a release-note row for consumers that switch on
    the outcome.
- **(b) The same check in the usage service only.** This is smaller, but the race stays.
- **(c) Report the newer candidate without a new outcome.** This is the smallest, but it hides why
  nothing was saved.

#### FB63. Agent Framework history and a service that keeps the conversation

`NhAiAgentFrameworkAdapter` sets `ChatHistoryProvider` from `ChatHistoryProviderFactory`. With the OpenAI
Responses API, which keeps the conversation itself, Microsoft Agent Framework 1.19 throws "Only
ConversationId or ChatHistoryProvider". The package has three switches on `ChatClientAgentOptions`, all on
by default: `ThrowOnChatHistoryProviderConflict`, `WarnOnChatHistoryProviderConflict` and
`ClearOnChatHistoryProviderConflict`. `NhAiAgentCreateRequest` has no way to set them.

- **(a) Pass the three switches through (advice).**
  - Three init-only properties on `NhAiAgentCreateRequest` map one to one onto the package's switches,
    and their defaults stay as they are.
  - Guidance says that history the service keeps lives at the provider, outside NewHeap's retention and
    forgetting, so only a deployment whose data may live there switches the error off.
- **(b) One `ServiceManagesHistory` flag** that leaves the history provider out and fails fast when the
  service does not keep the conversation. This is simpler, but it is not what the package offers.
- **(c) A general callback on `ChatClientAgentOptions`.** It could replace tools or the client and so
  bypass governance; not advised.

### C. The database read tool

#### FB64. MySQL 5.7 reported as a permission problem

The MySQL provider's first statement is `SELECT VERSION(), CURRENT_ROLE();`, and it checks the version only
afterwards (`VerifyReadOnlyPrincipalAsync`). `CURRENT_ROLE()` exists from MySQL 8.0, so on 5.7 the statement
fails first: 1370 maps to `permission-denied` and 1305 to `database-failure`. The guidance and the tool's
README promise `provider-version-not-supported` before the principal is verified. The tests call
`EnsureSupportedServer("5.7.44-log")` directly, and the containers are MySQL 8.4 and MariaDB 11.4 only.

- **(a) Check the version from the handshake, before anything that needs 8.0, and test it on a 5.7
  container (advice).**
  - The flavor and minimum check runs right after the connection opens, on
    `MySqlConnection.ServerVersion`, so schema reads and queries refuse 5.7 as well.
  - `CURRENT_ROLE()` runs after that check.
  - A test against `mysql:5.7` proves `provider-version-not-supported` with exit code 3.
- **(b) The same fix with a unit test of the order only.** This is faster in CI, but no 5.7 server ever
  runs the path.
- **(c) Map 1370 and 1305 to `provider-version-not-supported`.** This is wrong for a real permission
  problem on 8.0.

#### FB65. One profile for MySQL and MariaDB

A profile names `mysql` or `mariadb`, and the provider is built for that flavor before it connects. A
server of the other flavor fails with `provider-flavor-mismatch`. Statement limits, role activation and
grant parsing all follow the fixed flavor.

- **(a) An additive profile value `mysql-compatible` that detects the flavor (advice).**
  - The flavor comes from the server version at connect time, and each flavor keeps its own minimum
    (MySQL 8.0, MariaDB 10.11).
  - `provider` reports the flavor that was detected.
  - `mysql` and `mariadb` stay strict.
- **(b) Let `mysql` detect the flavor.** A `mysql` profile pointed at MariaDB would then pass, against the
  current rule and SPM-367.
- **(c) Only guidance:** one profile per flavor.

### D. Delivery

#### FB66. Train

- **(a) Train 31 with six lanes (advice).**
  - PR: FB56, FB57 and FB58, in the protected resource and the outbound guard.
  - TB: FB59.
  - CA: FB60 and FB62.
  - FU: FB61.
  - AF: FB63.
  - MY: FB64 and FB65.

  PR and TB both touch `NhAssistantMcpConnections`, so TB merges after PR. The full matrix runs once per
  train.
- **(b) The small fixes first (FB60, FB62, FB64) as a fix of their own, then a train for the rest.**
  This ships the bugs sooner, but costs a second verification round.

## Lane choices

| # | Choice | Status |
|---|---|---|
| CA1 | FB60's sample evidence is the SPM-372 sample host, which runs in Development (scope validation on) and now registers a catalog per scope over the tools a run imported next to the overview. The sample API has no catalog that is scoped by nature, so no scoped catalog was added to `Program.cs`; the library test proves that a step's tool of a scoped catalog runs with the turn's own services. | for Lars to review |
| CA2 | FB62's compare-and-set keeps only a candidate of the same asset with a newer version (`newer-proposed`) or the same version and hash (`already-proposed`). A proposal still discards a candidate that names no asset or another asset, such as an administrator's own candidate, as every new version did before; an administrator's own save still replaces any candidate. Protecting an administrator's candidate from proposals would need yet another outcome and was left out. | for Lars to review |
| CA3 | The race is closed without a lock or new schema: the check runs in the save's transaction after the latest version is read, so a proposal that saved first is seen or makes the save a unique-index conflict, and `AddAssetCandidateAsync` tries a conflict again, at most three times in all. A proposal that loses three times in a row answers `refused` with `assistant-version-conflict`, as before. | for Lars to review |
| CA4 | A proposal saves through the registered `INhAssistantAgentConfiguration`. When that is the built-in `NhAssistantAgentConfiguration`, it uses the internal compare-and-set entry point, which hands back the store's kept candidate. When the application replaced it, for example with a decorator that adds its own checks or audit, the save goes through the replacement as before. The check before the save still answers `newer-proposed` there, but the store no longer decides between two proposals at once. The guidance and the release note tell such an application to run one proposal at a time. A test with a replacing decorator proves that the decorator receives the save on both providers. | for Lars to review |
| CA5 | The sample publishes a version 3 of the risk review's text, so SPM-351 shows version 3 replacing the candidate of version 2 and a late version 2 answering `newer-proposed`; SPM-320's list of texts now shows three versions (version 1 still the only deprecated one). | for Lars to review |
