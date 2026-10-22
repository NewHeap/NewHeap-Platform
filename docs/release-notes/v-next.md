# v-next

## NewHeap.Platform.AI.Chat.AspNet: assistant endpoints built once at host start

`MapNewHeapAssistant` built every assistant endpoint twice when a host started: the authorization
middleware's policy cache reads the application-wide endpoints while the pipeline is built, and the
routing middleware reads them again through a composite of its own on the first request, while a
route group creates its minimal API endpoints anew on every read. The assistant's route groups now
build their endpoints once and give every reader the same endpoints. The first request no longer
pays for a second build (about 180 ms for the 110 endpoints in a Debug test host).

| Breaking change | Required action |
|---|---|
| Routing, the authorization policy cache, link generation and API descriptions share the assistant endpoints built on the first read; conventions added to the group that `MapNewHeapAssistant` returns after the host started no longer apply. | None. Configure that group before the host starts. |

## NewHeap.Platform.AspNet.Common: authentication endpoints built once at host start

The endpoints that `UseNhAuthentication` maps are now built once, like the assistant's, and
routing, the authorization policy cache, link generation and API descriptions share them. Before,
every host start built them a second time on the first request.

| Breaking change | Required action |
|---|---|
| Conventions added to the authentication endpoints after the host started no longer apply. | None. Add route metadata in `IConfigurableAuthenticationEndpoint.Configure`. |

## NewHeap.Platform.Media.Http: media endpoints built once at host start

`MapNhMediaEndpoints` now builds the media endpoints once and gives routing, the authorization
policy cache, link generation and API descriptions the same endpoints, instead of building them
again on the first request.

| Breaking change | Required action |
|---|---|
| Conventions added to the group that `MapNhMediaEndpoints` returns after the host started no longer apply. | None. Configure the group before the host starts. |

## NewHeap.Platform.AspNet.Proxy: management API endpoints built once at host start

`MapProxyEndpoints` now builds the endpoints of `/newheap-proxy/api` once; the branch's routing and
the application-wide endpoints share them instead of each building their own.

## NewHeap.Platform.AI.Common, NewHeap.Platform.AI.AspNet.Common and NewHeap.Platform.AI.AspNet.Mvc: tool catalogs validated after the endpoints are registered

Fix. The startup validation of the tool catalogs (the platform's catalog checks, the API bridge
validator and the MCP export check) ran as hosted services. A web application in the minimal
hosting model starts its hosted services before it builds its pipeline, so reading the API
bridge catalog built ApiExplorer's descriptions while the application's endpoints were not yet
registered. ApiExplorer keeps those descriptions for the life of the process, so ApiExplorer and
OpenAPI documents missed every minimal API endpoint, and the application-wide endpoint data source
was initialized empty and built all endpoints again for every data source added afterwards. A
host with a server now runs these checks once, after its application pipeline is configured and
before the server starts; a host without a server still runs them while its hosted services
start. An invalid catalog fails the host's start with the same exception as before.

| Breaking change | Required action |
|---|---|
| In a web host the AI tool catalog checks run while the web host service builds the pipeline instead of at their own place among the hosted services. A host that captures startup errors (`CaptureStartupErrors`, as under IIS) serves its startup error page for an invalid catalog instead of stopping. | None. Do not read the API bridge catalog or ApiExplorer from your own hosted services before the pipeline is built. |

## NewHeap.Platform.AI.AspNet.Common and NewHeap.Platform.AI.AspNet.Mvc: delegated reads under UseMvc

Fix. The delegated read restriction refuses a token whose target is unknown when it is
authenticated. `UseNewHeapPlatformAspNetCommon` serves controllers through the old `UseMvc`
routing, where a controller request has no endpoint, so in such a host every gateway read of a
headless agent run was refused and returned `api-bridge-unauthenticated`. When the gateway is
enabled, the API bridge now adds a global MVC authorization filter that runs first and
authenticates the delegated read scheme with the action that MVC executes as the target, in both
routing modes. A delegated read principal still reaches only the `GET` gateway action its token
names, with its invocation header, and a request whose target is not known is still refused. A
scheme that middleware authenticates before MVC, such as the default authentication scheme under
`UseMvc`, stays refused there; such a host routes its controllers with `UseEndpointRoutingOnly()`.

| Breaking change | Required action |
|---|---|
| With the gateway enabled, MVC authenticates the delegated read scheme before its other authorization filters for every `GET` request with the `X-NewHeap-AI-Invocation` header, and answers `401` when a delegated read principal reaches another action than the one its token names. | None. |

## NewHeap.Platform.AspNet.Common: controllers with endpoint routing only

New option, no breaking change. `NewHeapAspNetCommonOptionsBuilder.UseEndpointRoutingOnly()`
(`NewHeapAspNetCommonOptions.UseEndpointRoutingOnly`) routes controllers with endpoint routing only:
MVC keeps `EnableEndpointRouting` on, and `UseNewHeapPlatformAspNetCommon` routes before CORS and
authentication, maps controllers with `MapControllers` and no longer adds `UseMvc`. Controller
requests then carry endpoint metadata, so the authorization middleware, `[EnableCors]` and
`[DisableCors]` and other endpoint-metadata middleware apply to controllers, and the host builds one
route table instead of two. Map conventional routes in the `UseEndpoints` callback with
`MapControllerRoute`; combining the option with `UseMvc(Action<IRouteBuilder>)`, or turning
`EnableEndpointRouting` off in `ConfigureMvc`, fails at startup. The default is unchanged. New
applications opt in; an existing application first checks authorization handlers that read
`AuthorizationFilterContext`, filter-based authorization checks, generated links, Razor Pages and URL
rewriting in `UseMiddlewares` (guidance rule `nh-backend-platform-pipeline`).
