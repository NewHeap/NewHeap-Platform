# NewHeap.Platform.AI.AspNet.Mvc

Publishes authorized ASP.NET Core MVC controller actions as governed NewHeap AI
tools. Every tool call runs through `INhAiToolInvoker` and then through the
application's own HTTP pipeline as the calling user, so the controller's
authentication, `[Authorize(Policy = ...)]` attributes, model binding, validation
and filters stay the authorization boundary.

## Install

Reference `NewHeap.Platform.AI.AspNet.Mvc` from the API project. The API must
already register `AddNewHeapPlatformAIAspNet`, a budget manager and an
idempotency manager (non-read tools require idempotency), and MVC ApiExplorer
through `AddControllers` or `AddEndpointsApiExplorer`.

## Register

```csharp
builder.Services.AddNewHeapPlatformAIMvcBridge(bridge => bridge
    .UseToolSetId("sample-api")
    .UseSelfBaseUrl(builder.Configuration["NewHeap:AI:Bridge:SelfBaseUrl"])
    .IncludeControllers("Project", "ProjectTask")
    .ExcludeActions("Project.CreateRolledBackSample")
    .RequireExplicitPolicy(true)
    .UseInnerDiscoveryPolicy<ProjectAiToolDiscoveryPolicy>()
    .EnableMcpExposure()
    .WithToolDefaults(defaults =>
    {
        defaults.MaxResultBytes = 65_536;
        defaults.TimeoutSeconds = 30;
        defaults.MaxInputBytes = 16_384;
    }));
```

`UseSelfBaseUrl` also accepts a resolver (`provider => ...`); without a value the
bridge reads `NewHeap:AI:Bridge:SelfBaseUrl`. `NewHeap:AI:Bridge:Enabled=false`
keeps the registration but publishes no tools. The registration adds:

- `NhAiMvcBridgeToolCatalog`, an attested runtime catalog built once from
  ApiExplorer and validated at startup with `NhAiToolCatalogAttestation`;
- `NhAiMvcBridgeDiscoveryPolicy`, which shows a bridge tool only when the current
  user satisfies every policy of its action and delegates all other tools to the
  inner policy (default: deny);
- `INhAiMvcBridgeExecutor`, the self-HTTP executor using the named client
  `NhAiMvcBridgeDefaults.HttpClientName` (`newheap-ai-bridge`);
- a startup validator that fails fast on configuration errors and logs the
  number of published tools.

Call `WithNewHeapPlatformAITools()` on the MCP server builder to export tools
created with `EnableMcpExposure()` through `/mcp`; the agent adapter accepts the
catalog like any generated catalog.

## Conventions

| Descriptor field | Rule |
| --- | --- |
| Id | `<toolset>.<controller-kebab>.<action-kebab>`, plus `-by-<route-parameters>` when two actions of a controller share a name |
| Export name | `<toolset>_<tool id with "." as "_">_v<version>`, at most 64 characters |
| Effect | GET read-only, PUT/PATCH idempotent mutation, POST mutation |
| Approval | read: policy-controlled; every other effect: required |
| Idempotency | required for every non-read; the lease key is sent as `Idempotency-Key` |
| Policies | named policies of the action and its controller |
| Description | `[NhAiBridgeTool(Description)]`, then the XML `summary`, then `EndpointSummary`/`EndpointDescription` |
| Contract hash | SHA-256 over method, route template, input schema and policies |

The input is one flat object: route values and query primitives are top-level
properties and a complex body is `body`. Collection actions (a query model with
`Page`, `ItemsPerPage`, `OrderBy`, `Filter` and `Search`) publish `page`,
`itemsPerPage`, `search`, `orderBy` and `filter` and are sent in the NewHeap query
contract. Schemas come from `JsonSchemaExporter` with string enums and
`[Required]`/`[Description]` annotations. Derive from
`NhAiMvcBridgeDefaultConventions` and register it with `UseConventions` to change
tool ids, descriptions, the query encoding or the body serializer.

`[NhAiBridgeTool]` may only narrow a tool: `Exclude`, a stricter `Effect`, lower
`MaxResultBytes` or `TimeoutSeconds`, or `RequireApproval = true` on a read.

## Result

Tools return `TaskResult<NhAiBridgeResponse>`:

```json
{ "status": 200, "contentType": "application/json", "body": { }, "truncated": false, "bodyBytes": 1234 }
```

A body over `MaxResultBytes` becomes a `bodyText` fragment with
`truncated: true` and the hint "Use paging or filters to reduce the result."
HTTP failures map to `NhAiBridgeFailureCodes`: `api-bridge-validation` (400/422,
with the model state as data), `api-bridge-unauthenticated`,
`api-bridge-forbidden`, `api-bridge-not-found`, `api-bridge-conflict`,
`api-bridge-upstream` (5xx, other statuses, transport) and `api-bridge-timeout`.
Failure messages and logs never contain response body text.

## Security defaults

- Only actions with a named policy are published (`RequireExplicitPolicy(true)`).
- Anonymous, `[NonAction]`, file-upload and `DELETE` actions are never published.
  `IncludeDeleteActions(true)` fails at startup in v1 because destructive tools
  require a verifier. `IncludeFileUploads(true)` accepts files as base64 input,
  bounded by `MaxInputBytes`.
- Every non-read tool requires approval and an idempotency key.
- Only `Authorization` (the caller's own bearer token from
  `INhAiCallerCredentialAccessor`), `Accept-Language`, `Idempotency-Key` and
  `X-NewHeap-AI-Invocation` are forwarded. Cookies are not. The token never
  enters `NhAiInvocationContext`, audit records or logs.
- Redirects are not followed and the invoker's timeout cancels the HTTP call.
- Replacing `INhAiToolDiscoveryPolicy` after the bridge registration fails at
  startup; configure other tools with `UseInnerDiscoveryPolicy`.

## Limitations

- DELETE and other destructive operations are not supported in v1; use a curated
  tool with a verifier.
- Only attribute-routed actions that ApiExplorer describes with a single HTTP
  method are published.
- Header, service and complex non-collection query models other than their
  simple properties are not part of the tool input.
- The self-HTTP call requires a base URL the application can reach itself; route
  the named client to an in-process handler in tests.
- Prefer curated generated tools for multi-endpoint workflows, domain-specific
  approval summaries, verifiers or results that need reshaping for a model.
