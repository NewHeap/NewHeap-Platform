# ASP.NET Endpoint Routing Decision Note

This note prepares a decision: should `UseNewHeapPlatformAspNetCommon` stop combining the old
`UseMvc` routing with `MapControllers` and route controllers through endpoint routing only? It
records a startup measurement, the consumer impact of the move and the options. Nothing in the
platform changed for this note. It was checked against `main` at `6c152e0b` on 2026-10-21.

## Status

Decision pending. The recommendation is [option B](#options).

## Current pipeline

`AddNewHeapPlatformAspNetCommon` registers MVC with `MvcOptions.EnableEndpointRouting = false`.
`UseNewHeapPlatformAspNetCommon` then builds this pipeline:

```text
UseAuthentication → … → UseMvc(MvcConfigureRoutesAction) → UseRouting → UseAuthorization
  → UseEndpoints(MapControllers, background-operation hub, EndpointRouteConfigureAction)
```

`UseMvc` sees every request first. It serves every controller action through its own attribute
route tree (an `IRouter`), so a controller request never reaches `UseRouting`. `MapControllers`
builds a second set of controller endpoints that only link generation, API descriptions, the
authorization policy cache and requests that `UseMvc` did not match, such as a request with
another HTTP method, use. Minimal APIs, hubs and the authentication, media and assistant
endpoints pass through `UseMvc` without a match and are routed by `UseRouting`.

No test host uses `UseNewHeapPlatformAspNetCommon`; only the sample's `Program.cs` does. The
library and sample test hosts compose endpoint-routed pipelines of their own, so the choice below
does not change test durations.

## Measurement

A temporary test in the sample test project (removed afterwards) built a Kestrel host with
`AddNewHeapPlatformAspNetCommon`, the sample API's and the platform's controllers (104 actions) and
the platform's pipeline, once with and once without `UseMvc`. Every run was a separate process
(Debug build, loopback HTTP); each variant ran three times, alternating. The runs agreed within
2 ms.

| Stopwatch | `UseMvc` + `MapControllers` (today) | `MapControllers` only |
|---|---|---|
| `StartAsync`, including the pipeline build | 28–29 ms | 28–29 ms |
| First controller request (401) | 73 ms | 71–73 ms |
| First minimal API request after it | 24–25 ms | 4 ms |
| From the pipeline configuration until both are warm | 218–221 ms | 196–198 ms |
| Warm controller request | 1.5–1.6 ms | 1.2 ms |

The second route structure costs about 20 ms once per process: the attribute route tree is built
on the first request of any kind, and the endpoint matcher on the first request that `UseMvc` does
not serve. A warm controller request was about 0.3 ms slower in this Debug host (one sample per
run, so indicative only), and every non-controller request also searches the attribute route tree
first. Performance alone is a small argument for the move.

## Consumer impact of endpoint routing only

Verified items say so; the others follow from ASP.NET Core's documented behavior and need a
check in the opt-in spike.

1. **Conventional routes.** `NewHeapPlatformAspNetCommonOptionsBuilder.UseMvc(Action<IRouteBuilder>)`
   and `NewHeapPlatformAspNetCommonApplicationBuilderOptions.MvcConfigureRoutesAction` are public.
   A consumer's `MapRoute` and `MapAreaRoute` calls move to `UseEndpoints` as
   `MapControllerRoute` and `MapAreaControllerRoute`. The sample does not use them.
2. **Endpoint metadata for controllers (verified).** `HttpContext.GetEndpoint()` is `null` in a
   controller request today, in filters and in the action. Everything that reads endpoint
   metadata therefore ignores controllers: the authorization middleware, rate limiting, output
   caching, request timeouts and endpoint CORS metadata. The move makes those apply to
   controllers, which fixes some setups and changes others where such attributes were added
   without effect.
3. **AI delegated reads.** `NhAiDelegatedReads` refuses a delegated read token when the request's
   endpoint is unknown, by design. Under `UseMvc` a bridged controller `GET` has no endpoint, so a
   host that uses `UseNewHeapPlatformAspNetCommon` likely refuses every delegated read. The
   library and sample tests compose endpoint-routed hosts and do not cover this pipeline. Not
   verified end-to-end; it should be checked regardless of this decision.
4. **Authorization filters.** With `EnableEndpointRouting = false`, MVC turns `[Authorize]` into an
   `AuthorizeFilter` and resolves the named policies when it builds the action descriptors
   (verified: an unknown policy name fails there for the whole application). With endpoint
   routing the authorization middleware authorizes from endpoint metadata before MVC runs and
   resolves policies per request. Consequences:
   - `AuthorizationHandlerContext.Resource` becomes the `HttpContext` instead of MVC's
     `AuthorizationFilterContext`. The platform's handlers do not depend on it
     (`ActiveDivisionAccessHandler` reads `INhDivisionScopedResource` or the request); consumer
     handlers that cast the resource break.
   - Challenges and forbids come from `IAuthorizationMiddlewareResultHandler` instead of action
     results; a consumer's custom result handler starts to apply to controllers.
   - Code that looks for `IAllowAnonymousFilter` or `AuthorizeFilter` in the filter list must read
     `IAllowAnonymous` and `IAuthorizeData` endpoint metadata instead.
5. **CORS attributes.** The platform calls `UseCors` before `UseRouting`, where the CORS middleware
   cannot read endpoint metadata. `[EnableCors]` and `[DisableCors]` on controllers, which MVC
   applies itself today, would need `UseCors` between `UseRouting` and `UseEndpoints`.
6. **Route precedence.** Today attribute routes win over every endpoint-routed endpoint because
   `UseMvc` runs first. With one route table, overlapping routes resolve by order and precedence,
   and equal matches fail with `AmbiguousMatchException` (500) at request time.
7. **Link generation.** Under `UseMvc`, `IUrlHelper` in controllers and views is the `IRouter`-based
   helper; with endpoint routing it uses `LinkGenerator`, which reuses ambient route values
   differently. The platform and sample back ends use no `Url.Action`, `CreatedAtAction` or
   `CreatedAtRoute`; consumers who do should compare the generated URLs. `LinkGenerator` itself
   already sees the controllers today. `RazorViewService` renders templates with an empty
   `RouteData`; links inside templates need the same check.
8. **Razor Pages.** `AddMvc` registers Razor Pages, and `UseMvc` routes page actions through its
   attribute routes; `MapControllers` does not. An application with pages, such as scaffolded
   Identity UI, needs `MapRazorPages`. The platform and sample have none.
9. **Areas and the proxy.** The proxy's administration area already uses endpoint routing in its
   own branch (`MapAreaControllerRoute`) and is unaffected.
10. **MVC bridge and API descriptions.** The bridge builds its catalog from ApiExplorer and action
    descriptors, and OpenAPI reads the same; neither depends on the routing mode. Its self-HTTP
    hop is routed by whichever pipeline the host has. Only delegated reads (item 3) depend on the
    endpoint.
11. **Middleware placement.** `AddMiddlewaresAction` runs before routing and sees no endpoint,
    today and after the move. Middleware added after `UseNewHeapPlatformAspNetCommon` runs after
    the terminal endpoints in both modes.

## Options

| Option | What changes | Consumer impact | Cost |
|---|---|---|---|
| A. Keep both | Nothing; document that controllers carry no endpoint metadata. | None. | About 20 ms per process and a small per-request overhead stay; endpoint-metadata features, delegated reads included, do not work for controllers. |
| B. Opt-in endpoint routing now (recommended) | A registration option, for example on `NewHeapAspNetCommonOptions` because `EnableEndpointRouting` is set when the services are registered, keeps endpoint routing on and skips `UseMvc`; conventional routes go through the existing `UseEndpoints` callback, and combining the option with `UseMvc(Action<IRouteBuilder>)` fails at startup. The sample opts in. | None by default. Opting in brings items 1–8 for that application. | One option, a pipeline test (controller, minimal API, authentication, hub and a delegated read), sample evidence and a guidance rule. |
| C. Switch in the next major | Endpoint routing becomes the only mode; `UseMvc(Action<IRouteBuilder>)` and `MvcConfigureRoutesAction` are obsoleted first and then removed. | Items 1–8 for every consumer, with a migration table. | Breaking release; best done after B has been used. |

Recommendation: **B now, C in the next major.** The startup gain alone does not justify a
breaking change, but one route table, controllers with endpoint metadata and working delegated
reads do justify a supported path. An opt-in lets consumers check their authorization handlers,
CORS attributes, Razor Pages and generated links before the default changes.

## Decisions for Lars

1. Choose A, B or C.
2. With B: the option's name and place (service registration or pipeline builder), and whether
   the sample's API opts in at once.
3. Independently of the choice: whether to verify and fix delegated reads (item 3) in a host that
   uses `UseNewHeapPlatformAspNetCommon` now.
