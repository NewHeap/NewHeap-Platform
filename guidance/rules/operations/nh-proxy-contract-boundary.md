---
id: nh-proxy-contract-boundary
title: "Register the proxy foundation and recognize remaining runtime gaps"
area: configuration
reference: runtime-configuration
summary: "Configure a WebApplication with AddNewHeapProxy and UseNewHeapProxy; Use maps native YARP endpoints and loads initial configuration internally. Managed rules, administration, and SQLite persistence remain unimplemented."
sample-cases: ["SPM-238"]
public-symbols: ["NhProxyRewriteRule", "NhProxyRedirectRule", "ConfigureYarp", "INhProxyConfigurationStore", "INhProxyDraftTester", "INhProxyLoginAuditStore", "AddNewHeapProxy", "UseNewHeapProxy", "MapNewHeapProxy"]
skills: ["newheap-runtime-configuration"]
providers: ["provider-neutral"]
risk: high
---
## Preferred approach

Treat the proxy packages as an unreleased foundation. Call `AddNewHeapProxy()`
for defaults, supply an options lambda, or pass the `NewHeapProxy` configuration
section (SQLite settings bind from its `Sqlite` child). Registration installs
`IOptions<NhProxyOptions>`, `IOptions<NhProxySqliteOptions>`, and YARP with an empty
in-memory configuration. Options are startup snapshots and do not reload.
Register once, build the `WebApplication`, call `app.UseNewHeapProxy()`, and run
normally. Use returns the same `WebApplication` and maps endpoints internally;
do not additionally call `MapNewHeapProxy`. Mapping delegates to native
`MapReverseProxy`, which installs the YARP endpoint source and loads initial
configuration. Do not add a separate YARP initializer. An empty provider creates
no forwarding routes; administration endpoints are not mapped yet.
Standalone `MapNewHeapProxy(IEndpointRouteBuilder)` is a lower-level alternative
for hosts that only need endpoint mapping. Managed redirect processing will be
added to `UseNewHeapProxy` later. SPM-238 proves this
startup flow and typed contracts; SPM-239 records the remaining runtime gap.
Storage stubs are not registered and no database file is created.

Call `NhProxyOptions.ConfigureYarp(yarp => { ... })` in host code to supply an
`Action<IReverseProxyBuilder>` using YARP's native builder extensions, such as
`ConfigureHttpClient`. Repeated calls replace the callback; null arguments are
rejected. The read-only `YarpConfiguration` callback is excluded from JSON and is not a
database/appsettings rule setting. It executes once per registration after base
setup, never on rule reload. SPM-238 exercises it through `AddNewHeapProxy`.
Host customization does not make unimplemented managed proxy or
draft execution available.

Use separate rewrite and redirect save requests with an expected revision.
Each rewrite cluster has one `Destination`, and configuration collections are
immutable. Typed transforms preserve operation order through JSON. Draft inputs
contain synthetic request data and both engine revisions, never administrator
cookies. Use the existing `TaskResult` contract for expected validation/conflict
outcomes; inspect activation separately from a committed save.

Storage contracts live in the neutral proxy library. SQLite composition and
concrete storage stubs live in `NewHeap.Platform.AspNet.Proxy.Sqlite`. Fixed-account
credentials are host options and do not belong in configuration or audit documents.
Inbound forwarded-header trust remains host-owned. Login auditing has its own
append/query/retention boundary and cannot modify rule revisions.

## Avoid

- Interpreting successful compilation or contract tests as working proxy behavior.
- Using only `AddNewHeapProxy` and assuming service registration installs route endpoints.
- Calling `MapNewHeapProxy` again after `UseNewHeapProxy` has already mapped endpoints.
- Catching `NotImplementedException` and reporting persistence or managed activation success.
- Adding multiple destinations, load balancing, affinity, or coupled engine activation.
- Persisting raw YARP configuration, mutable snapshots, credentials, or diagnostic exceptions in public results.
- Claiming SQLite, SQL Server, or PostgreSQL runtime evidence from these contract tests.

## Verification

Run the two proxy library test projects and the sample's
`ProxyContractBoundarySamplesTests`. Verify host startup, options, the callback,
and empty YARP configuration. Requests must reach host endpoints after `UseNewHeapProxy`.
Mapping must materialize native YARP route endpoints from initial configuration.
SQLite operations must fail explicitly until implemented; JSON tests prove typed transform preservation.
Replace the relevant stub checks with behavior tests as features arrive, extend
SPM-239 with real executable evidence, and keep its library-gap status until then.
