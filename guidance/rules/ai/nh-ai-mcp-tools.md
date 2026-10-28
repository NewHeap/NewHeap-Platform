---
id: nh-ai-mcp-tools
title: "Expose generated AI tools through MCP"
area: backend
reference: ai-mcp-tools
summary: "Adapt an explicitly MCP-exposed generated NewHeap catalog through the official SDK without duplicating domain code or bypassing discovery and invocation authorization."
sample-cases: ["SPM-222", "SPM-240", "SPM-370", "SPM-371"]
public-symbols: ["INhAiMcpToolAdapter", "WithNewHeapPlatformAITools", "CallNewHeapToolAsync", "CallNewHeapFlatToolAsync", "NhAiMcpToolException", "NhAiMcpResultMetadata", "NhAiToolAnnotationHints", "NhAiToolExportSchema", "NhAiToolHint", "NhAiToolJsonSerializerOptions", "INhAiChangingToolCatalog", "NhAiToolCatalogViews", "NhAiMcpExposureBuilder", "NhAiMcpProtectedResourceOptions", "NhAiMcpProtectedResourceDefaults", "NhAiMcpProtectedResourceTokenValidatedContext"]
skills: ["newheap-backend-development"]
providers: ["provider-neutral"]
risk: high
---
## Preferred approach

Add `NewHeap.Platform.AI.Mcp` and `NewHeap.Platform.AI.AspNet.Common`, then call
`WithNewHeapPlatformAITools` on the official `IMcpServerBuilder`. Mark only
reviewed tools with `NhAiToolExposure.Mcp`; keep local and MCP exposure explicit
flags on the same generated contract. The integration resolves an authenticated
invocation context for every list and call request, then creates MCP tools per actor
and tenant through `INhAiMcpToolAdapter`. The adapter asks the shared default-deny
discovery service for visible descriptors and wraps the same generated
`AIFunction` delegates used for local execution.

Configure the official MCP transport, authentication, and endpoint in the host.
For HTTP, prefer the official stateless Streamable HTTP transport unless a
reviewed feature genuinely requires session state. Keep official authorization
filters enabled where applicable, but retain the NewHeap discovery policy and
invocation gate: transport authorization does not replace application resource
authorization.

To let MCP clients outside the application sign in, make the exposure a protected
resource of the host's authorization server: call
`WithNewHeapPlatformAITools(exposure => exposure.UseProtectedResource(...))` with
`NhAiMcpProtectedResourceOptions.Resource`, the public URL of the MCP endpoint, the
`AuthorizationServers`, issuer identifiers exactly as their metadata names them, and the
`ScopesSupported`, and map the endpoint with
`RequireAuthorization(NhAiMcpProtectedResourceDefaults.AuthorizationPolicy)`. An MCP client
that follows the MCP authorization flow then finds the authorization server from the
endpoint, obtains a token for the resource there and signs in; nothing depends on one
client. The exposure serves the protected resource metadata (RFC 9728) with `resource`,
`authorization_servers`, `scopes_supported` and `bearer_methods_supported` at the
resource's well-known address (`/.well-known/oauth-protected-resource` followed by the
resource's path) and at the root. It answers an MCP request without a valid token with
`401` and `WWW-Authenticate: Bearer resource_metadata="...", scope="..."`, adding
`error="invalid_token"` when the request sent a token, and accepts only JWT access tokens
of those servers whose audience is the resource (RFC 8707), validated with the keys their
metadata publishes. The authorization server, the registration of clients and the consent
stay the host's; NewHeap is not an authorization server.

Compositions and the script tool, tools whose descriptor names a
`CompositionFeature`, are part of the exposure only when it opts in:
`WithNewHeapPlatformAITools(exposure => exposure.AllowCompositions().AllowScripts())`. Even then each
`tools/list` and `tools/call` asks the application's switch once per feature: the assistant's
`INhAssistantCompositionSwitch` for the channel `Mcp`, with the caller as person and no agent, or,
on a host without the assistant, `NewHeap:AI:Assistant:Compositions` and `:Scripts` as configuration
has them now. While the switch has a feature off its tools are not listed and a call is refused with
`assistant-composition-off` or `assistant-scripting-off` before it runs; a feature the exposure does
not allow answers `ai-tool-not-authorized` like any tool it does not export. Individual tools never
change (`nh-ai-assistant`, SPM-371).

The token's person is the host's decision. Without `OnTokenValidated` the request signs in
with the token's claims (`iss`, `sub`, `scope`, `client_id`), which
`UseAuthenticatedClaimsWithoutTenant(issuer)` projects for a host without accounts of its
own. A host with its own accounts replaces the principal in `OnTokenValidated`, for
example with the account linked to the token's issuer and subject and that account's
current claims, and calls `Fail` for a subject linked to no account. The resource requires
no scope of a token; map a granted scope to a capability with
`AddScopeCapability("scope", value, capability)` when a tool needs one. The discovery
policy, the invocation gate and each tool's own policies still decide what the person may
see and call. Name the host's own schemes in `AcceptHostSchemes` to keep its own clients
signing in at the same endpoint: only a token that none of the authorization servers issued
reaches them, so a token of those servers is accepted for this resource only.

A token for the resource is never passed on. `INhAiCallerCredentialAccessor` returns no
token for such a request, so an API bridge tool, which calls the application's own API with
the caller's token, fails with `api-bridge-unauthenticated` over it; expose generated or
code-owned tools to these clients. The token signs in nowhere else either: the scheme
authenticates only on endpoints that require the policy.

Behind a reverse proxy, `Resource` is the public URL, which may differ from the app's own
route. The metadata is served at the well-known address of the public path, matched against
the full path of the request as the app sees it, and at the root; the challenge always names
the public metadata address built from `Resource`, never one built from the request's host or
forwarded headers. Let the proxy forward `/.well-known/oauth-protected-resource` followed by
the public path, and the root when the origin serves this resource alone, to the app
unchanged; `NhAiMcpProtectedResourceDefaults.MetadataAddressOf(resource)` names that address.
A stateless exposure maps only `POST`, so a client that probes with `GET` finds the metadata
at the well-known addresses, as RFC 9728 lets every client; the assistant's discovery probes
with an MCP `initialize` `POST` and reads the challenge's scopes. The metadata document allows any
origin; a browser-based client also needs the host's CORS policy to expose
`WWW-Authenticate`.

Expected `TaskResult` failures remain structured tool results and are marked as
MCP tool errors rather than JSON-RPC protocol failures. An enveloped export
publishes the shared `NhAiToolResult` shape as structured content and its schema,
built from the descriptor's output schema, as the output schema. Cancellation propagates.
Unexpected exceptions are handled once at the protocol boundary without
returning internal exception detail.

For a typed .NET client that intentionally calls a generated NewHeap tool, use
`CallNewHeapToolAsync<TInput, TOutput>`. Pass the generated tool input directly;
the helper creates the required `input` envelope and returns the deserialized
`data` of the tool result. A failed tool result becomes `NhAiMcpToolException`,
which preserves the original `CallToolResult` for structured handling. Use the
official `CallToolAsync` API directly for independently registered MCP tools,
including consumer-owned write tools that do not use the generated NewHeap
contract. Supply explicit serializer options when a source-generated JSON
context or null-omission policy owns the wire schema.

When an external wire contract already owns the argument and result shape of a
generated tool, declare `ExportSchema = NhAiToolExportSchema.Flat` on that tool
next to its `NhAiToolExportName`. The MCP export then publishes the input type's
properties as the top-level arguments and the output type as the structured
result; a failed `TaskResult<T>` that carries typed data publishes that data as
the structured error payload. The input type must be an object; the generator
rejects other input types with `NHAI012`. Local and agent execution keep the
generated envelope, and the governed `AIFunction` still runs through the shared
invoker. Call a flat tool through `CallNewHeapFlatToolAsync<TInput, TOutput>` with
the tool set's serializer options, or through the official `CallToolAsync` with
the input properties as arguments.

A flat export writes its structured result, text block and typed error payload
with the tool set's declared `JsonSerializerContext`. Without a declared context it
uses `NhAiToolJsonSerializerOptions.FlatExport`: camelCase names, string enums,
relaxed escaping, compact output and every property written, including nulls.
Declare a context when another wire contract applies; a byte-identical external
contract should always declare one. Enveloped exports without a context keep the
Microsoft.Extensions.AI defaults, which omit nulls.

The generated input and output schemas describe the effective serializer
contract. They follow the declared context's naming policy, default ignore
condition and enum converter, or the defaults above. `JsonIgnore` without a
condition removes a property; `Never` keeps it required; `WhenWritingNull` and
`WhenWritingDefault` keep it optional; `WhenWriting` and `WhenReading` remove it
only from the output or input schema respectively. Correcting a schema changes the
schema and contract hash, so treat it as a contract change for pinned manifests
and bound proposals.

Every failed governed invocation publishes its stable failure detail. The
`_meta` object carries `NhAiMcpResultMetadata.CodeKey`, `MessageKey`,
`EvidenceReferenceKey` when present, and `TypedPayloadKey`. A failure without
typed data publishes structured content `{ code, message, evidenceReference }` and
the text `code: message`, for flat and enveloped exports alike. The invocation
pipeline owns stable `NhAiToolFailureCodes` such as `ai-tool-budget-denied`; a
consumer failure keeps its own result code. Both client helpers throw
`NhAiMcpToolException` with `Code`, `FailureMessage`, `EvidenceReference` and
`PayloadJson`, or `NhAiMcpToolException<TPayload>` with the typed `Payload` when
the failure carried one. Catch the base type unless the typed payload is needed.

A failed result also carries the format and arguments of its message, so a client
can translate it. `NhAiMcpResultMetadata.MessageFormatKey`
(`newheap.com/message-format`) holds the message's composite format, such as
`Project '{0}' has {1} open tasks.`, and `MessageArgumentsKey`
(`newheap.com/message-arguments`) a JSON array of exactly the values the text
shows. Strings, booleans and numbers stay JSON primitives; a `DateTime` or
`DateTimeOffset` becomes ISO 8601 to the second and a `DateOnly` `yyyy-MM-dd`, with
the invariant culture; an argument with a format specifier such as `{0:N2}`, and an
argument of any other type, becomes the text it shows. The text `code: message`,
the structured `{ code, message }` and `MessageKey` do not change. Both keys are
published together, for flat and enveloped exports and next to a typed payload
alike, and only when the format filled with the arguments gives the text exactly,
with at most `MaxMessageArguments` (16) arguments of at most
`MaxMessageArgumentLength` (256) characters and a format of at most
`MaxMessageFormatLength` (1,024) characters; otherwise neither is. A message that
joins several messages, or starts with the field name of a result without a code,
carries neither. A fixed message, such as a pipeline refusal or a refusal of the
export itself, is its own format without arguments; the export never publishes
what the application's resolver reported in its place. A failed imported MCP tool
keeps the remote result, its `_meta` included, as `data` under its own fixed message.

Declare a message that a client should translate as a `FormattableString` before
passing it to `TaskResult`: an interpolated string passed straight to `Failed`,
`AddError` or `WithError` binds to their `string` overload, so its rendered text
becomes its own format without arguments, as NewHeap's string localizer sees it
too. Both client helpers expose the keys as `NhAiMcpToolException.FailureMessageFormat`
and `FailureMessageArguments`; look up the client's own text for the format, fill in
the arguments with the client's culture, and fall back to `FailureMessage` when the
format is unknown.

MCP annotation hints follow the governance effect. When a client-facing hint must
be more cautious than the effect, declare `ReadOnlyHint`, `DestructiveHint`,
`IdempotentHint` or `OpenWorldHint` on the tool. An override may only narrow: it
can publish `destructiveHint: true` for a consumer-authoritative mutation, but it
can never claim read-only, idempotent, non-destructive or closed-world behavior
the effect does not imply. The generator rejects a widening override with
`NHAI013`, runtime validation rejects hand-built widening descriptors, and the
audit record lists declared overrides in `AnnotationOverrides`. A hint never
changes approval, idempotency or verification.

Other libraries may register independently governed MCP tools through
`WithTools`, `WithToolsFromAssembly`, or manual `McpServerTool` services. Keep
their export names distinct from every NewHeap-managed export. Startup rejects
name collisions and SDK registrations whose metadata identifies them as a
NewHeap tool, because those form a second publication path. Only source-generated
`INhAiGeneratedToolCatalog` implementations and runtime catalogs that implement
`INhAiAttestedToolCatalog` and pass `NhAiToolCatalogAttestation` at startup, such as
the API bridge catalog, enter the NewHeap export path. Any other catalog with an
MCP-exposed descriptor fails at startup and on every list or call. A catalog
without MCP exposure, such as a run-scoped imported MCP catalog that only serves
agents, is skipped by the export path and may share the host. Catalogs may be
registered as scoped services; startup validation resolves them in a scope of its
own.

A runtime catalog whose descriptors change while the application runs, such as the
tools of consultations that administrators define as data, implements
`INhAiChangingToolCatalog` and is registered as a singleton. Every change is a new,
immutable view (`Current`) that is attested on its own; discovery calls
`RefreshAsync` first, and the agent framework and the MCP export read descriptors and
functions from one view (`NhAiToolCatalogViews.Of`), so a change between the two never
breaks a listing. A view that fails its attestation, or a tool that would take the
export name of a tool registered at startup, stays out of the export instead of
failing it, and startup never fails on such data. When `MayExportToMcp` is true,
`WithNewHeapPlatformAITools` signals `notifications/tools/list_changed` through the
MCP SDK's tool collection whenever the catalog raises `Changed`. Over Streamable HTTP
only a stateful server (`Stateless = false`, which needs session affinity across
instances) declares `listChanged` and tells its sessions: clients of the initialize
handshake (2025-11-25) hear the change on their standalone GET stream. A stateless
server, the recommended setup and the SDK's default, declares no `listChanged`, and its
`subscriptions/listen` grants no tool list changes, so its clients see a change on their
next `tools/list`. Every listing and call reads the view in effect, so a call of a tool
from a stale list fails with `ai-tool-not-authorized` and runs nothing.

## Avoid

- Adding separate MCP methods that copy generated domain tool implementations.
- Maintaining application-specific lists of generated tool names solely to wrap
  input and unwrap `TaskResult<T>` responses.
- Registering a consumer-owned write through the MCP SDK because the generated
  export envelope does not match its wire contract; declare a flat export schema
  on the generated tool instead.
- Declaring a stronger governance effect only to change an MCP annotation hint;
  declare a narrowing hint override instead.
- Parsing failure text or message templates for a code; read the result code from
  `_meta` or the client exception.
- Parsing failure text for its values, or translating it by its rendered text; read
  the message format and arguments from `_meta` or the client exception.
- Passing an interpolated message that clients should translate straight to
  `TaskResult.Failed`; declare it as a `FormattableString` so its format and
  arguments survive.
- Calling independently registered MCP tools through the NewHeap client helper.
- Publishing a NewHeap tool both through its generated catalog and through SDK tool registration.
- Giving an external MCP tool the same wire name as a NewHeap-managed export.
- Publishing every local tool remotely or treating catalog membership as authorization.
- Building one global actor-independent tool list for a multi-tenant host.
- Trusting MCP arguments, prompts, retrieved content, or headers as scope authorization.
- Returning credentials, internal exception text, prompts, or raw audit content over MCP.
- Using MCP Tasks as NewHeap's authoritative durable-operation store.
- Passing an MCP client's access token on to the application's own API or another
  server; it is for the MCP resource only.
- Accepting a token at the MCP endpoint whose audience is not the resource, for example
  through a host scheme that trusts the same authorization server without an audience.
- Building an authorization server, client registration or consent into the MCP
  exposure, or a sign-in for one particular MCP client; the MCP authorization flow is the
  contract, and the authorization server stays the host's.
- Deriving the resource or its metadata address from the request's host behind a proxy.

## Verification

Use the official in-memory stream transport to list and invoke the generated
tool without a network or live model. Verify an unauthorized context receives no
NewHeap tool, a direct call still passes the invocation gate, cancellation
propagates, failed `TaskResult` values remain structured tool errors, an external
SDK tool with a distinct name remains available, and an export-name collision
fails at startup. Start the host in the Development environment next to a
run-scoped agent-only catalog and assert that only generated MCP tools are
listed, while a hand-written catalog with MCP exposure still fails at startup.
Also call the generated tool through
`CallNewHeapToolAsync<TInput, TOutput>`, omit an optional null property through
explicit serializer options, assert the typed data is returned, and assert a
failed result becomes `NhAiMcpToolException` without changing direct SDK calls.
For a flat export, assert the listed input schema exposes the input properties
without an `input` wrapper, that the structured result is the output type without
a `success` envelope, and that a typed denial remains available as the structured
content of the failed `CallToolResult`. Snapshot the flat text byte for byte with
and without a declared context, including a null property. Deny the invocation gate
and assert both flat and enveloped exports publish `{ code, message }`, the text
`code: message` and the code in `_meta`. Assert the client exception exposes the
code, message, evidence reference and typed payload, that a narrowing hint is
listed and audited, and that a widening hint fails with `NHAI013`. Fail a tool with
a `FormattableString` message and assert that the text stays `code: message` while
`_meta` and the client exception carry its format and arguments, also over
Streamable HTTP, that joined messages carry neither, and that a refusal of the
export carries its fixed text without arguments. With `UseProtectedResource`, assert the
metadata at both addresses and its fields, also behind a path prefix, the challenge without a
token and `error="invalid_token"` with an invalid, expired (`NhAiTestAuthorizationServer.IssueAccessToken`
with an issue time) or foreign-audience token, that a token for the resource signs in while a
token of the same server for another audience never reaches a host scheme, that the token is
never passed on, that a person without rights meets the tools' own refusals, and that a host
without the option serves no metadata and keeps its own challenge. Prove a round trip in
which a client that follows the MCP authorization flow, such as the MCP SDK client or the
assistant's own client, discovers the exposure, registers and signs in at
`NhAiTestAuthorizationServer`. For an exposure with `AllowCompositions` or `AllowScripts`, assert
that a marked tool is listed and runs while the switch is on, is left out and refused with its code
while it is off, and that a feature the exposure does not allow is never listed. SPM-222, SPM-240,
SPM-370 and SPM-371 are the executable references.
