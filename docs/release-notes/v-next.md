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
