---
id: nh-backend-platform-pipeline
title: "Compose the platform's ASP.NET pipeline"
area: backend
reference: backend-platform-pipeline
summary: "Register the platform with AddNewHeapPlatformAspNetCommon and build its pipeline with UseNewHeapPlatformAspNetCommon; new applications route controllers with endpoint routing only."
sample-cases: ["SPM-097"]
public-symbols: ["AddNewHeapPlatformAspNetCommon", "UseNewHeapPlatformAspNetCommon", "NewHeapAspNetCommonOptionsBuilder", "UseEndpointRoutingOnly", "NewHeapPlatformAspNetCommonOptionsBuilder", "NewHeapPlatformAspNetCommonApplicationBuilderOptions"]
skills: ["newheap-backend-development"]
providers: ["provider-neutral"]
risk: high
---
## Preferred approach

Register the platform once with `AddNewHeapPlatformAspNetCommon` and build the request pipeline
once with `UseNewHeapPlatformAspNetCommon`, which adds the trace identifier, health checks, the
platform's CORS policy, authentication, HSTS and HTTPS redirection, request localization, the
no-follow header, routing, authorization and the endpoints (controllers, the background-operation
hub and the `UseEndpoints` callback). Map application endpoints in that callback or on the
`WebApplication` afterwards.

In a new application, call `UseEndpointRoutingOnly()` on `NewHeapAspNetCommonOptionsBuilder`.
Controllers then share one endpoint-routing table with minimal APIs, hubs and the platform's own
endpoints instead of being served by the old `UseMvc` routing. Routing runs first, before CORS and
authentication, so endpoint metadata applies to controllers too: the authorization middleware
authorizes them, the CORS middleware applies `[EnableCors]` and `[DisableCors]` next to the
platform's CORS policy, and middleware that reads endpoint metadata, such as rate limiting, output
caching or request timeouts added with `UseMiddlewares`, sees controllers too. Map conventional
routes in the `UseEndpoints` callback with `MapControllerRoute` or `MapAreaControllerRoute`.

Without the opt-in, the default stays: MVC runs with `EnableEndpointRouting` off, `UseMvc` serves
every controller through its own route tree and endpoint routing serves everything else, so
`HttpContext.GetEndpoint()` is `null` in a controller request. Before an existing application opts
in, check the differences:

- Authorization handlers receive the `HttpContext` as their resource instead of MVC's
  `AuthorizationFilterContext`; challenges and forbids come from the authorization middleware.
- Code that looks for `AuthorizeFilter` or `IAllowAnonymousFilter` must read `IAuthorizeData` and
  `IAllowAnonymous` endpoint metadata.
- Overlapping routes resolve in one table; equal matches fail with `AmbiguousMatchException`.
- `Url.Action`, `CreatedAtAction` and `CreatedAtRoute` use `LinkGenerator`; compare the links.
- Razor Pages need `MapRazorPages` in the `UseEndpoints` callback.
- Middleware from `UseMiddlewares` runs after routing; rewrite URLs before
  `UseNewHeapPlatformAspNetCommon`.

## Avoid

- Combining `UseEndpointRoutingOnly()` with `UseMvc(Action<IRouteBuilder>)`, or turning
  `EnableEndpointRouting` off in `ConfigureMvc`; both fail at startup.
- Adding rate-limiting, output-cache or request-timeout metadata to controllers under the default
  routing; without the opt-in that middleware never sees a controller endpoint.
- Calling `UseRouting`, `UseAuthentication` or `UseAuthorization` again around
  `UseNewHeapPlatformAspNetCommon`.

## Verification

Run `PlatformPipelineRoutingTests`: in both routing modes a controller answers `401` without a
token, `403` without the permission and `200` with it, the platform's CORS policy answers preflight
requests for allowed origins only and adds its header to the response, and only with
`UseEndpointRoutingOnly` does the controller request carry endpoint metadata and bypass `UseMvc`;
the opt-in refuses `UseMvc` routes and `EnableEndpointRouting` turned off at startup.
`NhAiMvcBridgePlatformPipelineTests` shows a delegated gateway read in both modes. SPM-097 opts in
in the sample API's `Program.cs`.
