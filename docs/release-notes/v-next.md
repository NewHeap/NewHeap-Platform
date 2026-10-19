# v-next

## NewHeap.Platform.Common and NewHeap.Platform.AspNet.Common: finding actors by name

`INhActorSearch` finds actors whose name or e-mail address contains a query
(`SearchAsync(NhActorSearchRequest)`, at least two characters, at most `Limit` results, users by
default). NewHeap Identity registers a search over its users that ignores case and leaves out
locked-out users; an application with its own identity model registers its own with
`AddNewHeapActorSearch<T>()`, which NewHeap Identity does not replace. Without a search, modules ask
for an actor's id. The assistant's administration uses it to choose the account an agent answers to.

| Breaking change | Required action |
|---|---|
| None; the interface and its registration are new. | Register `AddNewHeapActorSearch<T>()` in an application without NewHeap Identity to offer accounts by name. |

## NewHeap.Platform.Common and NewHeap.Platform.AspNet.Common: secrets that fail closed

A `${Secrets:…}` reference in appsettings whose secret is missing from the secrets file stays as
literal text. For the JWT signing key that text, which anyone can read in appsettings, became the key
tokens were signed and validated with. `NhJwtSigningKey` now refuses a missing key, an unresolved
reference and a key shorter than 32 bytes, so `AddNewHeapPlatformAspNetCommon` (and
`ConfigureNhJwtBearerValidationOptions` and `NhAuthenticationService`) stop with a message that names
the setting, never its value. The key is read from the setting itself or else from `Secrets:` in the
secrets file.

`NhConfigurationSecrets.Read(configuration, key)` reads any secret the same way and returns `null` for a
blank value or an unresolved reference; `IsUnresolvedReference` and `FindUnresolvedReferences` (keys
only) are available for other checks. `AddNewHeapPlatformCommon` registers a startup check that logs a
warning naming every setting that still holds an unresolved reference. Webhook triggers of
`NewHeap.Platform.AI.Chat.AspNet` refuse a secret that is still such a reference, as they refuse one
shorter than 32 characters.

| Breaking change | Required action |
|---|---|
| The application no longer starts when the JWT signing key is missing, an unresolved `${Secrets:…}` reference or shorter than 32 bytes. | Set a random key of at least 32 bytes, for example 64 random bytes in Base64, as `NewHeap:PlatformAspNetCommon:Authorization:JWT:Token:Key` in the secrets file, and replace every token signed with the old value. |
| A webhook trigger whose secret is an unresolved reference refuses its calls. | Configure the secret. |

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: agents outside the chat, accounts by name and agents created by applications

An agent that only works on its own can be kept out of the chat: `ConfigureAgent(id, run =>
run.HideFromChat())` in code, or `offeredInChat: false` on `POST` and `PUT admin/agents` for an agent
created in the administration (`OfferedInChat` on `NhAssistantAdminAgentInputDto`; omitted keeps it
on update, a new agent is offered). It is left out of the status and `GET agents` lists, and `POST
conversations` answers `assistant-agent-not-found` for it; its triggers, runs, subject threads,
consultations and existing conversations keep working. `NhAssistantAdminAgentDto` reports
`OfferedInChat`.

The run catalog explains its choices: `ApprovalPolicySummaries` gives per approval policy who
decides (the initiator, a role on the subject or an authorization policy), how long a proposal
waits, four eyes and whether the run waits (`NhAssistantApprovalPolicySummaryDto`,
`NhAssistantApproverSummaryDto`), and `AccountSearch` tells whether accounts can be found by name.
`GET admin/accounts?query=` searches them through `INhActorSearch` (`NhAssistantAccountDto`; without a
search `assistant-account-search-unavailable`), and `GET admin/accounts?ids=` returns the names the
application knows for stored account ids.

`INhAssistantAgentConfiguration.CreateAgentAsync(NhAssistantAgentRequest, actorId)` creates an agent
the way an administrator does, for example from a package import; it then gets run settings, skills
and triggers as data as before.

Apply the `AgentChatAvailability` migration of `NewHeap.Platform.AI.Chat.SqlServer` or
`NewHeap.Platform.AI.Chat.PostgreSql`: it adds `HiddenFromChat` to `AssistantAgent`. Existing agents
stay offered in the chat.

| Breaking change | Required action |
|---|---|
| Database schema change. | Apply the `AgentChatAvailability` migration. |
| `INhAssistantAgentConfiguration` has the new member `CreateAgentAsync`. | Implement it in your own implementations and test doubles. |
| `NhAssistantAdminAgentInputDto` has the new optional parameter `OfferedInChat`. | Recompile code that constructs the record. |

## @newheap/platform-ai-chat: a run configuration that explains itself

The run configuration of `@newheap/platform-ai-chat/admin` was reworked after a review in a consuming
application:

- Every catalog choice is a searchable picker that shows a name with what it means; approval policies
  read as a sentence built from `approvalPolicySummaries`. The technical account is found by name
  (`NhAssistantAdminApiService.searchAccounts`, `getAccounts`) when the catalog reports
  `accountSearch`, and otherwise entered by id with the name shown next to it.
- Run settings are grouped as questions (on whose behalf, how it works, who approves, limits) with
  the rarely changed options under "Advanced".
- Forms check required fields and numbers before sending. Errors appear under their field, a
  form-level message appears next to the save button, the first invalid field gets the focus, and a
  failed action of a skill, trigger or agent shows its error at that item.
- An agent created in the administration that only chats is offered a guided setup that asks when
  it starts, what it does, on whose behalf, who approves and its limits, and saves run settings, a
  skill and a trigger in shadow or puts them live at once.
- The agent editor asks how a new agent is used: in the chat, on its own (`offeredInChat: false`)
  or both; a new agent that works on its own continues in that guided setup. The agent list shows
  "Not in the chat", names technical accounts and translates the names of agents in code.
- Number fields, time zones and the other controls share the administration's styling, and the
  forms keep to the width of their host.
- Names and descriptions of agents in code are translated everywhere in the administration; the
  agent editor shows the translations and sends the keys back unless the administrator changes the
  text. Skills show a readable name with their id and a translated description when the host has
  `nh-assistant.agents.<agentId>.skills.<skillId>.name` or `.description`; the policy an agent
  requires reads from `nh-assistant.policies.<policy>`, otherwise as it is named in code. People who
  changed something are shown by name.
- A skill's tool choice shows each of the agent's tool selectors with the tool's description or how
  many tools a pattern matches (`NhAssistantAgentRunConfigurationComponent` takes the catalog as
  `tools`) and says where an agent started by a trigger writes its answer.
- The reason of a change is labelled as a note for other administrators that the agent does not
  read, and confirmations offer "Cancel" instead of "Keep".
- An agent created in the administration, and its skills, return to their own setting instead of
  offering "Follow the code", and the confirmation says which mode that setting is.

`AdminAgent` and `AdminAgentInput` gain `offeredInChat`, `RunCatalog` gains `approvalPolicySummaries`
and `accountSearch`, and `AssistantAccount`, `ApprovalPolicySummary` and `ApproverSummary` are new.
The mock API of `@newheap/platform-ai-chat/testing` serves `admin/accounts` from the scenario's new
`admin.accounts`, policy summaries and chat availability. The new texts are bundled in English and
Dutch, including `assistant-account-search-unavailable`.

| Breaking change | Required action |
|---|---|
| The run configuration's markup changed: selects became pickers and the forms moved into child components. | Update tests and styles that target the old selects or element ids of the run configuration. |
| `NhAssistantAdminAgentEditorComponent` emits the new `workRequested` instead of `saved` for a new agent that works on its own. | Handle `workRequested` when you host the editor yourself; `NhAssistantAdminAgentsComponent` does. |
| The texts `nh-assistant.admin.keep` and `nh-assistant.admin.agents.state.reason-label` were removed; confirmations use `nh-assistant.admin.cancel` and changes `nh-assistant.admin.agents.state.note-label`. | Move host overrides of the removed texts to the new keys. |

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
other configuration. A `config:` value is read with `NhConfigurationSecrets.Read`, so it may also
come from the secrets files, and a blank value or an unresolved `${...}` reference counts as missing. A stored secret and a reference replace each other.
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
The token that planning a connection obtains serves its first request. `McpBackgroundUseChanged` audits
consent, and `McpDelegatedTokenIssued` audits issuing once per turn, server, person and outcome with the
number of tokens in the new `NhAssistantAuditEvent.TokenCount`, never the token itself, and the `McpServerCredentials` migrations add the server columns and the
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
`mcp-connections` with the scenario's `mcpConnections`. Like the server, the mock now refuses a
`bearer`, `api-key` or `basic` server without a stored secret or a reference
(`assistant-validation` with `secret: ["required"]`), and the editor no longer offers **Clear** for a
secret, which the server always refused for those modes; choosing a reference or a mode without a
secret removes the stored one.

| Breaking change | Required action |
|---|---|
| `McpServer` has the required fields `userName`, `secretReference`, `clientCertificateReference` and `transport`, and `McpAuthMode` and `McpHeadlessUse` have new values. | Add the fields to hand-built `McpServer` objects and handle the new values in exhaustive switches. |
| The mock API refuses `bearer`, `api-key` and `basic` servers without a secret or reference, and the server editor has no **Clear** button for a secret. | Give such servers a secret in mock scenarios and tests; tests that clicked **Clear** choose another mode instead. |
