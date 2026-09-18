---
id: nh-ai-api-bridge
title: "Expose authorized MVC actions as governed AI tools"
area: backend
reference: ai-api-bridge
summary: "Publish existing controller actions as governed NewHeap tools that run through the shared invoker and the application's own HTTP pipeline as the calling user, with the controller authorization as the boundary."
sample-cases: ["SPM-242", "SPM-243", "SPM-244", "SPM-255"]
public-symbols: ["AddNewHeapPlatformAIMvcBridge", "INhAiAttestedToolCatalog", "INhAiBridgeConventions", "INhAiBridgeCollectionContractProvider", "NhAiBridgeCollectionContract", "NhAiNewHeapCollectionContractProvider", "INhAiBridgeBodySerializer", "NhAiMvcNewtonsoftJsonBodySerializer", "INhAiBridgeTrustedQueryBindingProvider", "NhAiBridgeTrustedQueryBinding", "NhAiBridgeTrustedQueryBindingKind", "INhAiCallerCredentialAccessor", "INhAiMvcBridgeExecutor", "NhAiBridgeActionInfo", "NhAiBridgeFailureCodes", "NhAiBridgeHttpRequest", "NhAiBridgeInputException", "NhAiBridgeResponse", "NhAiBridgeToolAttribute", "NhAiMvcBridgeBuilder", "NhAiMvcBridgeDefaultConventions", "NhAiMvcBridgeDefaults", "NhAiMvcBridgeDiscoveryPolicy", "NhAiMvcBridgeToolCatalog", "NhAiMvcBridgeToolDefaults", "NhAiToolCatalogAttestation", "NhAiMvcBridgeGatewayBuilder", "NhAiMvcBridgeGatewayOptions", "NhAiBridgeQueryDescription", "NhAiBridgeFilterField", "NhAiBridgeResultField", "INhAiBridgeResourceDescriber", "NhAiBridgeResourceDescription", "NhAiBridgeResourceSummary", "NhAiBridgeResourcePresentationOptions", "NhAiBridgeResourcePresentation"]
skills: ["newheap-backend-development"]
providers: ["provider-neutral"]
risk: high
---
## Preferred approach

Add `NewHeap.Platform.AI.AspNet.Mvc` to an API that already registers
`AddNewHeapPlatformAIAspNet` and MVC ApiExplorer (`AddControllers` or
`AddEndpointsApiExplorer`), then call `AddNewHeapPlatformAIMvcBridge` with a
dash-case `UseToolSetId`, the application's own base URL through `UseSelfBaseUrl`
(or `NewHeap:AI:Bridge:SelfBaseUrl`) and explicit `IncludeControllers` patterns.
Keep `RequireExplicitPolicy(true)`: only actions with a named
`[Authorize(Policy = ...)]` on the action or controller become tools. Anonymous,
`[NonAction]`, file-upload and `DELETE` actions are never published by default.
`IncludeDeleteActions(true)` fails at startup in v1 because destructive tools
require a verifier; expose deletions through a curated tool instead.

The bridge deliberately departs from the local-tool rule that a tool must not call
a controller. It does so without bypassing anything: every call first runs through
`INhAiToolInvoker` (invocation gate, capabilities, effect policy, approval,
budget, idempotency lease, timeout and result bounds) and then through the API's
own HTTP pipeline as the signed-in user. The controller's authentication,
authorization, model binding, validation and filters remain the authoritative
boundary; the bridge adds governance, it never replaces it. The caller's bearer
token comes from `INhAiCallerCredentialAccessor` and is forwarded only as the
`Authorization` header; it never enters `NhAiInvocationContext`, audit or logs.
Only `Authorization`, `Accept-Language`, `Idempotency-Key` and
`X-NewHeap-AI-Invocation` are forwarded.

Descriptors follow the HTTP method: GET is read-only with policy-controlled
approval; PUT and PATCH are idempotent mutations and POST is a mutation, and every
non-read tool requires approval and an idempotency key whose lease key travels as
`Idempotency-Key`. Tool ids are `<toolset>.<controller>.<action>` in dash-case,
with `-by-<route-parameters>` added when two actions share a name, and export
names are `<toolset>_<tool>_v<version>`. Valid names remain unchanged; names over
64 characters retain a readable prefix and gain a deterministic hash suffix.
The manifest records the resulting mapping, and collisions still fail at startup
with both route templates. Descriptions come from
`[NhAiBridgeTool(Description)]`, then the XML `summary`, then `EndpointSummary`
and `EndpointDescription`. `[NhAiBridgeTool]` may only narrow a tool: exclude it,
declare a stricter effect, lower its result or timeout limits, or require approval
for a read.

The discovery policy the bridge registers first binds the discovery context to
the current authenticated actor (or an agent's accountable owner), then shows a
bridge tool only when that user satisfies every policy of its action, per request. Configure the
application's policy for curated and generated tools with
`UseInnerDiscoveryPolicy`; without it those tools are denied. Replacing
`INhAiToolDiscoveryPolicy` after the bridge registration fails at startup.

The bridge catalog is an `INhAiAttestedToolCatalog`. `NhAiToolCatalogAttestation`
validates at startup that it is `SharedInvoker`-governed, that every created
function is an `INhAiGovernedAIFunction` bound to its descriptor and that the
attestation hash matches the manifest. Only then may it enter the MCP export path
of `WithNewHeapPlatformAITools`, next to generated catalogs; call
`EnableMcpExposure()` to publish the tools through MCP.

Bridge functions share the governed-function argument handling: flat arguments
without the `input` envelope run once with the same self-HTTP request, an
envelope mixed with other properties returns `ai-tool-input-invalid` before any
HTTP call, and unknown flat properties still fail the bridge input schema as
`api-bridge-validation`.

Tool output is `TaskResult<NhAiBridgeResponse>` with `status`, `contentType`,
`body`, `truncated` and `bodyBytes`. A body larger than `MaxResultBytes` is
returned as a `bodyText` fragment with `truncated: true` and a paging hint. HTTP
failures become `NhAiBridgeFailureCodes` such as `api-bridge-forbidden`; a `400`
keeps the model state as data, and no failure message contains response text.
Body serialization is independent of conventions. System.Text.Json web defaults
remain compatible through `INhAiBridgeConventions.SerializeBody`; use
`UseBodySerializer<NhAiMvcNewtonsoftJsonBodySerializer>()` to use the application's
MVC Newtonsoft settings from the current request scope, or register another
`INhAiBridgeBodySerializer`.

For a large API, call `EnableGateway` instead of offering the model one tool per
action. The gateway publishes four read-only tools, `<set>.search-resources`,
`.describe-resource`, `.query` and `.get`, over the read-only bridge actions grouped
per resource (`order`, `order-group`, ...). `search-resources` does a deterministic
text match on names, titles, summaries and field names and returns only resources
the current user may use. Describe the filter, order and result fields by
Canonical NewHeap endpoints need no consumer conventions. The built-in
`NhAiNewHeapCollectionContractProvider` recognizes canonical collection request
models and documented `CollectionResultModel<T>` or `SimpleCollectionResultModel<T>`
responses, then derives bounded filter, order, search and result metadata from the
same collection attributes and operator vocabulary used by the runtime. Ambiguous
canonical actions fail startup with an actionable response-metadata diagnostic.

For a legacy/custom endpoint, implement `INhAiBridgeCollectionContractProvider`
and register it with `AddCollectionContractProvider<T>()`. The provider supplies
recognition and field metadata and overrides only query encoding when its wire
contract differs; schema generation, validation, authorization and execution stay
in the library. Use `AddTrustedQueryBindingProvider<T>()` for actor, tenant or
active-scope values. These bindings resolve values only from audited invocation
scope, overwrite same-named model query values, merge collection filters without
letting model input weaken them and fail closed when scope is missing.

Add titles or summaries with `UseResourceDescriber`, or use
`UseLocalizedResourcePresentation<TResource>()` for consumer-owned `.resx` keys,
invariant-English fallbacks and startup validation of missing keys and orphaned
resource mappings. Resource ids remain invariant dash-case and separate from
presentation. `query` and `get` run the
underlying bridge descriptor through the shared invoker, so gate, policies, budget,
audit (with the underlying tool id) and the self-HTTP request are exactly those of
the bridge tool. Unknown and unauthorized resources fail identically with
`ai-tool-not-found`. Mutations are never reachable through the gateway; keep them
as explicit bridge or curated tools with approval, and combine both in agent tool
selectors such as `app-api-gateway.*` plus `orders.*`.

Prefer a curated generated tool when an operation spans several endpoints, needs
a domain-specific approval summary or verifier, is destructive, returns data that
must be reshaped or redacted for a model, or is a high-volume workflow that
benefits from a narrow contract. The bridge is the right choice for broad,
policy-protected CRUD and query surfaces whose controller contract is already the
product boundary.

## Avoid

- Publishing controllers without explicit policies or with `RequireExplicitPolicy(false)` on an API that relies on a fallback policy.
- Treating bridge discovery as authorization: the invocation gate and the controller still authorize every call.
- Forwarding cookies, custom headers or tokens other than the caller's own bearer token, or storing the token in the invocation context.
- Including DELETE actions or declaring a destructive effect through the bridge.
- Replacing the discovery policy after `AddNewHeapPlatformAIMvcBridge`; use `UseInnerDiscoveryPolicy`.
- Returning response body text in failure messages or logs.
- Recreating NewHeap collection operators or attribute reflection in a consumer conventions class.
- Letting model input provide actor, tenant or active-scope bindings; contribute them from authorized invocation scope.
- Replacing all bridge conventions only to select Newtonsoft body serialization or support one legacy collection wire contract.
- Routing mutations through the gateway or revealing in a message whether an unavailable resource exists.
- Hand-building a runtime catalog for MCP export without implementing `INhAiAttestedToolCatalog` and passing attestation.

## Verification

Host the API with its bridge in a test server, route the named
`NhAiMvcBridgeDefaults.HttpClientName` client into that server and assert the
descriptor snapshot, including excluded DELETE, anonymous, upload and policy-less
actions and a collision failure. Verify that a user without the policy neither
discovers nor invokes a tool, that a mutation without approval stops before the
HTTP call, that a `403` becomes `api-bridge-forbidden` without body text, that
truncation stays below the result limit, that a timeout returns
`api-bridge-timeout`, and that the outgoing request carries only the contracted
headers while context, audit and logs never contain the token. List and call the
tools through the in-memory MCP transport and assert an ungoverned attested
catalog fails validation. With the gateway enabled, assert per-user resource lists,
the described fields, that `query` and `get` send exactly the request of the bridge
tool and audit its id, that unknown and unauthorized resources fail identically,
that an unknown filter key fails before the HTTP call, trusted bindings cannot be
overridden, canonical collections need no custom conventions, legacy providers can
encode a different wire contract, localized resource metadata validates at startup,
export names stay deterministic and bounded, and that the gateway tools are
exported through MCP. SPM-242, SPM-243, SPM-244 and SPM-255 are the executable
references.
