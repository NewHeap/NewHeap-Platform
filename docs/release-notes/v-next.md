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
offers, and `ClearOnChatHistoryProviderConflict` removes the provider from the agent instance.

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
