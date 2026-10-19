# v-next

## NewHeap.Platform.AI.Mcp, NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: MCP servers in background runs

An administrator-connected MCP server is now offered in headless agent runs, and in the execution
of their deferred proposals, only when an administrator sets its new `headlessUse` to
`organization` (`NhAssistantMcpHeadlessUses`). Every server, existing and new, starts at `never`;
the `McpConnectionPolicy` migrations add the column. Before, servers with `none`, `bearer` or
`api-key` and no `requiredPolicy` were offered to background runs although the documentation said
they were not, and an approved deferred MCP proposal always failed with `assistant-approval-invalid`.
Now such a proposal imports the tool again and executes it once; a server that no longer allows
background runs fails it with `assistant-mcp-headless-not-allowed`. `organization` is refused for
`forward-user-token`. In a background run `requiredPolicy` is checked for the person whose rights the
run uses, and tools of an `organization` server also accept a code grant (`Grant("mcp.<server>.*")`).

The MCP client now attaches the credential to every HTTP request and checks every request against
the planned origin and `AllowedHosts`. A `403` with `error="insufficient_scope"` reports
`assistant-mcp-insufficient-scope` instead of `assistant-mcp-unauthorized`, also for tool calls, which
previously all reported `assistant-mcp-unreachable`. A tool list seen with one caller's forwarded
token no longer disables a tool for everyone. Tests, syncs and turns with the shared credential record
`lastConnectAt` and `lastConnectCode` on `NhAssistantMcpServerDto`. In `NewHeap.Platform.AI.Mcp`,
`NhAiMcpImportedToolPolicy.AllowAgentGrant` lets a code grant allow an imported tool, and
`NhAiMcpCallContext.Current` carries the authorized invocation context to the client transport while
the remote call runs.

| Breaking change | Required action |
|---|---|
| MCP servers are no longer offered in background runs unless `headlessUse` is `organization`. | Apply the `McpConnectionPolicy` migration; set `headlessUse` to `organization` for each server whose own credential agents may use without a person, and grant `mcp.<server>.*` to agents that act for themselves. |
| A forbidden MCP response with `insufficient_scope` reports `assistant-mcp-insufficient-scope`, and failed MCP tool calls report the connection code instead of `assistant-mcp-unreachable`. | Map the new code where you map `assistant-mcp-unauthorized`. |
| An imported tool with `AllowAgentGrant` has a different contract hash. | None for existing policies; approvals of a tool change when its server's headless use changes. |

## @newheap/platform-ai-chat: background runs per MCP server

The MCP server editor of `@newheap/platform-ai-chat/admin` gains **Background runs**: `Not in
background runs` (the default) or `Background runs with this credential`; servers that forward the
user's token offer the personal variant described below instead. The server list shows the choice and the last connection state, and a
connection test refreshes it. `McpServerInput.headlessUse` is optional (omitted keeps the stored
value), and `McpServer` gains `headlessUse`, `lastConnectAt` and `lastConnectCode`; the new type
`McpHeadlessUse` names the values. The codes `assistant-mcp-insufficient-scope`,
`assistant-mcp-headless-not-allowed` and `assistant-mcp-credential-mismatch` are bundled in English
and Dutch. The mock API of `@newheap/platform-ai-chat/testing` keeps `headlessUse` per server,
refuses `organization` for a forwarded token and records the connection state on test and sync.

| Breaking change | Required action |
|---|---|
| `McpServer` has the required fields `headlessUse`, `lastConnectAt` and `lastConnectCode`. | Add them to hand-built `McpServer` objects in tests. |

## NewHeap.Platform.AI.AspNet.Common, NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: MCP credentials and personal servers in background runs

MCP servers gain the authentication mode `basic` (`userName` plus the write-only secret as
password). A secret no longer has to be stored: `secretReference` `config:<name>` reads
`NewHeap:AI:Assistant:Mcp:Secrets:<name>`, and any other prefix is resolved by an
`INhAssistantMcpSecretResolver` that the application registers with
`AddMcpSecretResolver<TResolver>(prefix)`, for example for a vault. A reference is resolved for every
request, so a rotated secret applies at once, and its name contains no `:` or `..`, so it never reaches
other configuration. A `config:` value that is blank or still holds an unresolved `${...}`
reference counts as missing. A stored secret and a reference replace each other.
`clientCertificateReference` names a client certificate under
`NewHeap:AI:Assistant:Mcp:ClientCertificates:<name>` (a PKCS#12 file or a certificate store entry with
a private key) for mutual TLS. `transport` `auto`
(`NhAssistantMcpTransports`) falls back to the deprecated HTTP+SSE transport for servers without
Streamable HTTP; the SSE endpoint must stay on the configured origin.

`headlessUse` gains `personal` for `forward-user-token` servers. In a background run, such a server
receives a short-lived delegated NewHeap token of the person whose rights the run uses, minted for every
request, marked with the invocation and bound to the server's origin, and only after that person allowed
it: `GET mcp-connections` lists the person's servers and `PUT mcp-connections/{serverId}`
`{ allowBackgroundUse }` records or withdraws the consent, which a changed address or authentication
invalidates. Without consent the server is skipped with `assistant-mcp-consent-required`; a person who may
not sign in, a failing issuer or a host whose JWT bearer schemes do not restrict such tokens gives
`assistant-mcp-delegated-token-unavailable`. A call authorized for anyone but that person is not sent.
`NhAssistantMcpOptions.ForwardedTokenLifetime` (two minutes, at most five) sets the token's lifetime.
The audit kinds `McpDelegatedTokenIssued` and `McpBackgroundUseChanged` record issuing and consent
without the token, and the `McpServerCredentials` migrations add the server columns and the
`AssistantMcpConnection` table on SQL Server and PostgreSQL.

`NewHeap.Platform.AI.AspNet.Common` adds `AddNewHeapPlatformAIMcpDelegatedTokens`: every JWT bearer
scheme of the host refuses a token with the `nh-ai-mcp-delegated` claim unless `Accept(origin, path)`
names its audience and the request is below that path. `INhAiMcpDelegatedTokenIssuer` issues the tokens
(by default through `INhAuthenticationService.CreateDelegatedTokenAsync`), `NhAiMcpDelegatedTokenDefaults`
holds the claims and limits and `IsDelegatedTokenRestrictionEnforcedAsync` reports whether the host
applies the restriction. `AddNewHeapAssistant` calls it without `Accept`, so an assistant's host never
accepts its own delegated MCP tokens. These tokens are signed with the host's symmetric key: every host
that can validate them can also mint them, so use `personal` only between hosts that already share that
key.

| Breaking change | Required action |
|---|---|
| `AddNewHeapAssistant` wraps the events of every JWT bearer scheme, as delegated reads do. Tokens without the new claim are not affected. | None. A scheme with `EventsType` keeps working, but `personal` servers then get no tokens; set `Events` instead. |
| Startup fails when `ForwardedTokenLifetime` is not longer than zero or above five minutes. | None for the default. |
| New columns and the `AssistantMcpConnection` table. | Apply the `McpServerCredentials` migration. |

## @newheap/platform-ai-chat: MCP credentials and consent to background use

The MCP server editor offers **User name and password** (`basic`) with a user name, a choice between a
stored secret and a reference to a secret of the application, a client certificate name and the
transport, and **Background runs with the person's own account, after consent** (`personal`) for servers
that forward the user's token. The server list shows the reference, the certificate and the fallback
transport. The preferences show `nh-assistant-mcp-connections` (`NhAssistantMcpConnectionsComponent`)
when a personal server is assigned to an agent the user may use: one switch per server that allows or
withdraws background use at once. `NhAssistantApiService` gains `getMcpConnections` and
`updateMcpConnection`, with the models `McpConnection` and `UpdateMcpConnectionRequest`.
`McpAuthMode` adds `basic`, `McpHeadlessUse` adds `personal`, the new `McpTransport` names the
transports, `McpServerInput` gains the optional `userName`, `secretReference`,
`clientCertificateReference` and `transport`, and `McpServer` gains them as fields. The codes
`assistant-mcp-consent-required`, `assistant-mcp-delegated-token-unavailable`,
`assistant-mcp-secret-reference-invalid` and `assistant-mcp-certificate-reference-invalid` are bundled
in English and Dutch. The mock API validates the new fields, resolves `config:` references or the
scenario's `secretReferences`, knows the scenario's `clientCertificates` and serves
`mcp-connections` with the scenario's `mcpConnections`.

| Breaking change | Required action |
|---|---|
| `McpServer` has the required fields `userName`, `secretReference`, `clientCertificateReference` and `transport`, and `McpAuthMode` and `McpHeadlessUse` have new values. | Add the fields to hand-built `McpServer` objects and handle the new values in exhaustive switches. |
