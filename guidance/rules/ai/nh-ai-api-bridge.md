---
id: nh-ai-api-bridge
title: "Expose authorized MVC actions as governed AI tools"
area: backend
reference: ai-api-bridge
summary: "Publish existing controller actions as governed NewHeap tools that run through the shared invoker and the application's own HTTP pipeline as the calling user, with the controller authorization as the boundary."
sample-cases: ["SPM-242", "SPM-243", "SPM-244"]
public-symbols: ["AddNewHeapPlatformAIMvcBridge", "INhAiAttestedToolCatalog", "INhAiBridgeConventions", "INhAiCallerCredentialAccessor", "INhAiMvcBridgeExecutor", "NhAiBridgeActionInfo", "NhAiBridgeFailureCodes", "NhAiBridgeHttpRequest", "NhAiBridgeInputException", "NhAiBridgeResponse", "NhAiBridgeToolAttribute", "NhAiMvcBridgeBuilder", "NhAiMvcBridgeDefaultConventions", "NhAiMvcBridgeDefaults", "NhAiMvcBridgeDiscoveryPolicy", "NhAiMvcBridgeToolCatalog", "NhAiMvcBridgeToolDefaults", "NhAiToolCatalogAttestation"]
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
names are `<toolset>_<tool>_v<version>`. Id or export-name collisions fail at
startup with both route templates. Descriptions come from
`[NhAiBridgeTool(Description)]`, then the XML `summary`, then `EndpointSummary`
and `EndpointDescription`. `[NhAiBridgeTool]` may only narrow a tool: exclude it,
declare a stricter effect, lower its result or timeout limits, or require approval
for a read.

The discovery policy the bridge registers shows a bridge tool only when the
current user satisfies every policy of its action, per request. Configure the
application's policy for curated and generated tools with
`UseInnerDiscoveryPolicy`; without it those tools are denied. Replacing
`INhAiToolDiscoveryPolicy` after the bridge registration fails at startup.

The bridge catalog is an `INhAiAttestedToolCatalog`. `NhAiToolCatalogAttestation`
validates at startup that it is `SharedInvoker`-governed, that every created
function is an `INhAiGovernedAIFunction` bound to its descriptor and that the
attestation hash matches the manifest. Only then may it enter the MCP export path
of `WithNewHeapPlatformAITools`, next to generated catalogs; call
`EnableMcpExposure()` to publish the tools through MCP.

Tool output is `TaskResult<NhAiBridgeResponse>` with `status`, `contentType`,
`body`, `truncated` and `bodyBytes`. A body larger than `MaxResultBytes` is
returned as a `bodyText` fragment with `truncated: true` and a paging hint. HTTP
failures become `NhAiBridgeFailureCodes` such as `api-bridge-forbidden`; a `400`
keeps the model state as data, and no failure message contains response text.
Derive from `NhAiMvcBridgeDefaultConventions` when an API uses another collection
query encoding or body serializer, and register it with `UseConventions`.

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
catalog fails validation. SPM-242, SPM-243 and SPM-244 are the executable
references.
