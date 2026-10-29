# v-next

## NewHeap.Platform.AI.AgentFramework: a model service that keeps the conversation next to a history provider

`NhAiAgentCreateRequest` takes three init-only switches that set Agent Framework's
`ChatClientAgentOptions` switches of the same names: `ThrowOnChatHistoryProviderConflict`,
`WarnOnChatHistoryProviderConflict` and `ClearOnChatHistoryProviderConflict`, all `true` by default as
in Agent Framework. They matter when the model service keeps the conversation itself, as the OpenAI
Responses API does, while the agent has a history provider from `ChatHistoryProviderFactory`: by
default the run still fails after the model call with "Only ConversationId or ChatHistoryProvider".
History the service keeps lives at the model provider, outside the application's retention and
forgetting, so switch the error off only in a deployment whose conversation data may live there. The
session then follows the service's conversation id, and the provider is not used for it after that
first answer. `WarnOnChatHistoryProviderConflict` logs through the logger factory the model client
offers; because many clients, such as OpenAI's, offer none, NewHeap also logs one content-free warning
per created agent through the host's `ILoggerFactory`, naming only the agent and its version.
`ClearOnChatHistoryProviderConflict` removes the provider from the agent instance.

| Breaking change | Required action |
|---|---|
| None; the switches keep Agent Framework's defaults, so an agent without them behaves as before. | None. Set `ThrowOnChatHistoryProviderConflict = false` only where conversation data may live at the model provider. |

## NewHeap.Platform.DatabaseRead.Tool: the MySQL and MariaDB server check and `mysql-compatible`

A `mysql` or `mariadb` profile now checks the flavor and the minimum version (MySQL 8.0, MariaDB 10.11) on the
version the server reports while the connection opens, also behind MariaDB's `5.5.5-` prefix, before any
statement runs. Schema reads and queries against an older server, such as MySQL 5.7, now stop with
`provider-version-not-supported` (exit code 3), as documented, instead of a `database-query-failed` with
`permission-denied` (1370) or `database-failure` (1305) from `CURRENT_ROLE()` (SPM-367). Supported servers are
unchanged.

A profile's `provider` may also be `mysql-compatible`, for one profile that reaches a MySQL server in one
environment and a MariaDB server in another. The tool takes the flavor from that server version, keeps each
flavor's minimum, and follows the detected flavor for the statement limit, role activation, grant reading and
index evidence. `target.provider` and a database failure's `provider` report the detected `mysql` or `mariadb`;
`validate` and a failure before the server answered report `mysql-compatible`. `mysql` and `mariadb` stay strict
and still stop with `provider-flavor-mismatch` against the other flavor (SPM-367). An MCP wrapper or script that
switches on `provider` sees only the values it already knows once a server answered.

## NewHeap.Platform.AI.Common, NewHeap.Platform.AI.AspNet.Common, NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: one loopback-name rule

`localhost`, a `.localhost` name (RFC 6761) such as `auth.dev.localhost`, and a loopback IP literal are
loopback names everywhere the AI libraries accept plain http: the MCP protected resource
(`UseProtectedResource`) for its resource URL and its authorization servers, the assistant's OAuth options
(`RedirectUri`, `ClientUri`, `ReturnOrigins`) and the assistant's outbound guard for MCP servers and the
OAuth URLs of discovery, registration and the token endpoint (FB56). The outbound guard used to count only
the literal `localhost` and loopback IP addresses, so it refused `http://auth.dev.localhost` in every
environment. It now allows plain http to every loopback name where loopback is allowed (always in
Development, with `NewHeap:AI:Assistant:Mcp:AllowLoopback` outside Production), and connects over plain
http only when every address the name resolves to is a loopback address; a `.localhost` name that DNS
sends elsewhere is refused with `assistant-mcp-host-blocked`. The inbound protected resource has accepted
these names since 25.0.0: when it refuses a `.localhost` URL, the cause is another one, and its error
message names it. A protected resource whose URL or authorization server is an IPv6 loopback literal,
such as `http://[::1]:5281/mcp`, no longer fails at registration (SPM-331, SPM-299, SPM-370).

| Breaking change | Required action |
|---|---|
| Where loopback is not allowed (Production, or another environment without `Mcp:AllowLoopback`), the assistant now refuses an MCP server or OAuth URL on a `.localhost` name with `assistant-mcp-host-blocked`, also over https. | Give such a server a host name of its own, or turn on `NewHeap:AI:Assistant:Mcp:AllowLoopback` in a test environment. |
| Plain http to `localhost` or a `.localhost` name now connects only when every address the name resolves to is a loopback address. | Map the name to a loopback address, or use https for a server on another address. |

## NewHeap.Platform.AI.AspNet.Common: named protected resources

`UseProtectedResource(name, configure)` adds a protected resource next to the default one, for a second MCP
endpoint of the same exposure, for example `/partners/mcp` for other applications next to `/mcp` for AI
apps (FB57). A named resource has everything of its own: the scheme
`NhAiMcpProtectedResourceDefaults.AuthenticationSchemeFor(name)`, the policy `AuthorizationPolicyFor(name)`
to map its endpoint with, its `Resource` as the only audience it accepts, its authorization servers,
scopes, host schemes and `OnTokenValidated`, and its metadata at the well-known address of its own
resource URL. A token for one resource never signs in at another, even when the same authorization server
issued both, and the tokens of every resource are never passed on. The current `UseProtectedResource`
stays the default resource with the root metadata address. A name has lowercase letters, digits and
dashes and starts with a letter; a second resource with the same name, resource URL or metadata address,
and a host scheme that is a protected resource's scheme, are refused at registration (SPM-370).

## NewHeap.Platform.AI.AspNet.Common: a token the application marks as never passed on

`NhAiCallerCredentials.MarkNotForwardable(httpContext, issuer)` marks the token of the request as never
passed on (FB58). Call it from the `OnTokenValidated` event of a scheme of the application's own that
validates tokens issued for another resource, such as an MCP entry the application validates itself:
`INhAiCallerCredentialAccessor` then returns no token for the request, as it does for a token of a
protected resource, so an API bridge call fails with `api-bridge-unauthenticated` and a
`forward-user-token` MCP server gets no token. The mark lasts for the request and cannot be removed;
`NhAiCallerCredentials.IsNotForwardable(httpContext)` tells an accessor of the application's own to return
none as well. Prefer a named protected resource, which marks its tokens itself (SPM-370).

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: chain callers and context bindings the application stores

Links between applications that the application reviews while it runs no longer need a restart
(FB59). Register an `INhAssistantMcpLinks` with `UseMcpLinks<TLinks>()`: its `ListAsync` returns an
`NhAssistantMcpLinkList` with the chain callers whose consultation chains the application continues
and the context bindings of tools on administrator-connected MCP servers, as the application reviewed
and stores them. They count next to `ContinueMcpChain` and `AddMcpContextBinding`, which behave as
before. The assistant reads the source in a scope of its own and reuses its answer for
`NewHeap:AI:Assistant:Mcp:LinkCacheDuration` (`NhAssistantMcpOptions.LinkCacheDuration`, 30 seconds by
default, from zero to 5 minutes, on the application's clock), so a link the application adds or
revokes counts within that time. The application owns who may create a link.

Every stored entry is checked as the same registration in code is checked at startup. An entry that
registration would refuse is left out with a warning that names its reason
(`NhAssistantMcpLinkProblems`), and a stored entry never grants more than a registration could: a
stored binding applies only to a tool of an administrator-connected server under the binding's server
id and URL, never to a code-owned import, and a server id and tool or an issuer and client that code
registers keep their code registration. A source that throws or does not answer within
`NhAssistantMcpLinkList.ReadTimeLimit` (10 seconds) counts as no stored links until the next read,
while the code registrations still count. Adding or revoking a stored binding changes the contract of
that tool, so a proposal of it that waits for a decision no longer runs and the model proposes it
again; a call whose stored binding was revoked after its turn imported the tool stops with
`ai-mcp-binding-failed`. SPM-266 and SPM-366 show both kinds of link in the sample.

## NewHeap.Platform.AI.Chat: compositions next to a scoped tool catalog

A host with a composition (`AddComposition`) and a tool catalog registered as a scoped service failed to
start where it validates scopes, as Development does: "Cannot resolve scoped service ... from root
provider" (FB60). The composition catalog, a singleton, read the steps' tools from the root provider.
It now reads them once, at startup, from the catalogs of a scope of its own, which it disposes; only the
descriptors outlive it. A step still runs with the tool of the turn's own services. A step's tool must not
depend on the request: a step calls only a tool that a scope without a request has too, and a catalog
whose descriptors change while the host runs implements `INhAiChangingToolCatalog`, which compositions
leave out. Nothing changes for a host without scoped catalogs. SPM-372 shows it in the sample.

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: the newest skill candidate wins

`INhAssistantInstructionAssetUsage.ProposeCandidatesAsync` compared a proposed asset version only with the
live skill, and the store discarded the skill's candidate unconditionally, so proposing version 2 after
version 3, with version 1 live, replaced the candidate of version 3 with version 2; two proposals at once
could end the same way (FB62). The skill now keeps its newest candidate. The store decides in the save's
transaction (compare-and-set): a candidate of an older version of the asset, or one that names no asset
or another one, is discarded as before, while a candidate of a newer version stays. Such a proposal
answers the new outcome `NhAssistantInstructionAssetCandidateOutcomes.NewerProposed`
(`newer-proposed`) with the candidate's version, and saves and audits nothing for that skill. Of two
proposals at once, the newer version stays. An administrator's own save of a skill still replaces its
candidate. An application that replaced `INhAssistantAgentConfiguration`, for example with a decorator,
still receives every save of a proposal; there the check before the save answers `newer-proposed`, but
two proposals at once are not decided by the store, so run them one at a time. SPM-351 shows it in the
sample.

| Breaking change | Required action |
|---|---|
| `NhAssistantInstructionAssetCandidateDto.Outcome` can be `newer-proposed`, where a proposal of an older asset version used to replace a newer candidate and answer `proposed`. | Handle `newer-proposed` where you switch on the outcome, as nothing saved for that skill; it is not a failure. |

## NewHeap.Platform.AI.Common, NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: a person's chat continues after an execution report

`ExecuteInApplication(toolId, reportWithin, execution => execution.AlsoInline().ContinueChatAfterReport())`
(`NhAssistantApplicationExecutionBuilder.ContinueChatAfterReport`) continues a person's chat once the
application reported the execution of a proposal approved there, so the person no longer picks the work
up again after a slow execution. After a report of any outcome the assistant starts one turn in that chat
that nobody typed, after a two-second debounce on the application's clock, so reports that arrive
together reach one turn. The turn runs as the person whose turn made the proposal, never as a decider,
the chat's owner or another participant: `INhAiPrincipalFactory` rebuilds them in the active division of
that turn, the context resolver resolves them, and they must still be in the chat and pass the
assistant's access policy and the agent's policy. It can only propose: every tool effect other than a read
needs that person's approval in the chat, and a proposal waits for their decision like any other. The
report stays the application's information for the model. The turn's tools and audit events carry the
channel `execution-report` (`NhAssistantDecisionChannels.ExecutionReport`), and the audit records
`ChatFollowUpStarted`, or `ChatFollowUpSkipped` with the reason when the chat runs a turn or waits for a
decision or an answer, has a newer message than the report (`NhAssistantApplicationExecutionCodes.ChatAnswered`)
or the person fails a check; the report then waits for the person's next message. A refused or failed
hand-off and a passed report deadline start no turn. The debounce lives in the process. Propose-only
lasts until the person decides: once they approve a proposal of the follow-up, the turn that continues is
their own decision turn under the normal chat rules, so a tool that needs no approval may run there, as
after any approval. Off by default; a declaration with the switch and without `AlsoInline` is refused.

| Breaking change | Required action |
|---|---|
| `NhAssistantAuditEventKind` has the new values `ChatFollowUpStarted` (121) and `ChatFollowUpSkipped` (122), recorded only for a tool declared with `ContinueChatAfterReport`. | Let audit sinks with an exhaustive switch record or ignore them. |
