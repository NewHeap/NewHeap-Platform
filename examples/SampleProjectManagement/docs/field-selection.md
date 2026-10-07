# Opt-in collection field selection

SPM-028 and SPM-031 demonstrate scalar field selection through `ProjectController`
and `ProjectTableViewModel`. Start the sample with its normal local configuration
and use an authenticated request in Scalar or the HTTP client of your choice.

```http
GET /projects/fields
GET /projects?fields=id,name,status&itemsPerPage=20
GET /projects?fields=id,name,status&itemsPerPage=20&format=messagepack
```

The discovery response contains only authorized fields, their scalar type and
filter/order/search capabilities, plus supported formats. Fetch it when opening
the table settings. The server checks permissions again on every collection
request. Both discovery and selected responses use `Cache-Control: no-store`.

The normal collection envelope is retained. `items` contains only selected keys,
and `selection` records the original `requestedFields`, canonical deduplicated
`returnedFields`, and `format`. Selection metadata is present even on empty pages.
Names come from the Newtonsoft JSON contract, including `JsonProperty` aliases.
MessagePack uses standard maps, `application/x-msgpack`, the same names, string
dates/enums and browser number semantics. Decode the complete collection envelope.

## Compatibility boundary

Only the presence of `fields` activates the new path. A request without `fields`,
including one with only `format=messagepack`, uses the existing controller path.
An empty `fields` value is an invalid selection and returns `400`; it does not
fall back to the unrestricted response. Unknown and unauthorized fields produce
the same safe error. Invalid formats and unavailable query fields also return
`400` using a failed `TaskResult`, before querying the database.

**`FieldAccess` protects the opt-in path only. It does not restrict the legacy
endpoint.** Removing `fields` deliberately restores existing behavior. Do not use
this compatibility experiment as a security boundary for data currently exposed
by that legacy endpoint. Enforcing restrictions for every client requires a
separate, intentional migration of the legacy contract.

## Consumer implementation

1. Define a small table view model with `[Selectable]` scalar properties and an
   explicit `Expression<Func<TEntity, TView>>` member-initializer projection.
   Keep `[Filterable]`, `[Orderable]` and `[Searchable]` on supported query fields.
2. Use `[FieldAccess(Roles = "administrator,manager")]` for role OR checks or
   `[FieldAccess(Policy = "...")]` for an ASP.NET authorization policy. A property
   with both requires both; multiple attributes are AND-ed. A policy can resolve
   application services and inspect the supplied division/resource context.
3. Apply tenant/division/resource row restrictions to the source `IQueryable`
   before passing it to `NhFieldSelectionService.GetCollectionAsync`. Field access
   never replaces row access. The caller must authorize the resource context.
4. Branch on `NhFieldSelectionService.HasSelection(Request)`. Use
   `ToActionResult` for selected results and retain the original branch otherwise.
5. Expose `DescribeAsync<TView>` from a protected endpoint using the same resource
   context. Omit denied descriptors rather than revealing private field names.

The project sample uses roles for `OwnerUserId` and an active-division policy for
`Description`. The seeded project-manager and security-officer application roles
can select the owner; the seeded division editor's division permission grants the
description in its own division. Tests also demonstrate a custom resource-aware policy. Checks are
per request, with policy results reused only within that request. Per-row field
permissions are intentionally unsupported: scope rows first or expose a separate
contract where the authorization context is uniform. An arbitrary asynchronous
per-row handler cannot be pushed into SQL automatically.

Filters and ordering may reference authorized fields that are not selected for
output. Search uses only authorized searchable fields. Forbidden fields cannot
be probed indirectly through filters, sorting or search/counts. The sample query
keeps filtering, ordering and paging server-side and projects output fields
before materializing. Columns needed by predicates/order can still appear in SQL;
selection does not promise their absence from the database query plan.

This first version supports flat scalars, up to 64 requested fields and bounded
filter nesting. It rejects nested expansion, property converters and conditional
serialization. Do not add per-row database lookups or materialize entities before
selection. No database migration is required. Keep a deterministic default sort
including the row ID. Role-specific models are useful for materially different
contracts; do not duplicate a model for every combination of roles.

## Verification

```powershell
dotnet test src/Back-end/Tests/NewHeap.Platform.AspNet.Common.Tests --filter FieldSelectionTests
dotnet test examples/SampleProjectManagement/src/Back-end/Tests/SampleProjectManagement.Core.Tests --filter ProjectFieldSelectionSamplesTests
dotnet test examples/SampleProjectManagement/src/Back-end/Tests/SampleProjectManagement.Core.Tests --filter ControllerOpenApiMetadataTests
```

Run these commands from the repository root. The first command includes isolated
Testcontainers tests for SQL Server and PostgreSQL. Docker must be healthy; the
tests never connect to a consumer database. The translation-only tests separately
verify provider SQL composition without opening a connection. They are not a
substitute for the real database tests.

The release contract runs the full field-selection test group, including both
containers, on pull requests and common-package releases. Common preview packages
also require this check before packing. A provider failure blocks publication.

The OpenAPI check starts an isolated loopback HTTP host with the real controller
metadata and schema configuration, but without sample database services or jobs.
It verifies `/openapi/v1.json` and `/scalar/v1`, both JSON response variants, the
MessagePack media type, and the field-discovery endpoint. The sample operation
transformer describes the variants without changing the legacy runtime response.

See the [encoding benchmark](../../../src/Back-end/Benchmarks/NewHeap.Platform.AspNet.Common.Benchmarks/README.md)
for measured transport costs. JSON remains the recommended default.
