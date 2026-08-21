# NewHeap library sample plan

This is the coverage backlog for `SampleProjectManagement`. Its purpose is to make every public part of the NewHeap backend and frontend libraries discoverable through a concrete, executable sample. Related contracts and implementations may be covered together in one vertical case.

## Research

The inventory combines the public types from `NewHeap.Platform.Common` and `NewHeap.Platform.AspNet.Common`, every export from `nh-common`, and the implemented SampleProjectManagement applications. A heuristic scan found approximately 280 public backend declarations and 229 frontend exports. Selectors, pipes, extension methods, DI, and reflection are not always discoverable by type name. The sample therefore describes only demonstrable library behavior and contains no external implementation provenance.

## Definition of done

A case becomes a sample only when it is executable from Management or Workspace, uses real NewHeap types, explains its intent and request or response, and has a focused test or reproducible check. Backend modules include controller, service, entity, view model, mutate model, AutoMapper, repository, `DbSet`, and relationships where relevant, with authorization and no library-owned DAL migrations. Mutate models contain no audit fields. Mutating modal content uses `NhModalMutateBaseComponent`; other dynamic modal content may use `NhModalComponentImpl`. Large modals use `modalClasses: 'large'`. Pages, collections, and modal content derived from a NewHeap base use the `appOn...` extension points and do not override Angular hooks owned by the base. Documentation and AI instructions are English. Every executable UI has a complete English translation; additional languages use matching lowercase dash-case keys under the module object.

## Library sources

- `NH-BE`: [NewHeap.Platform.Common](../../../src/Back-end/Libraries/NewHeap.Platform.Common) and [NewHeap.Platform.AspNet.Common](../../../src/Back-end/Libraries/NewHeap.Platform.AspNet.Common)
- `NH-FE`: [public Angular API](../../../src/Front-end/projects/nh-common/src/public-api.ts)
- `CURRENT`: [current sample catalog](sample-catalog.md)

The complete public-surface mapping and intended sample entry points are documented in [newheap-surface-to-case-matrix.md](newheap-surface-to-case-matrix.md).

## 1. Domain, CRUD, and models

| ID | Case | Library surface | Verifiable outcome |
|---|---|---|---|
<!-- SAMPLE_CASES:1 -->

## 2. Collections, filtering, and projections

| ID | Case | Library surface | Verifiable outcome |
|---|---|---|---|
<!-- SAMPLE_CASES:2 -->

## 3. Mutations, partial and bulk operations, and validation

| ID | Case | Library surface | Verifiable outcome |
|---|---|---|---|
<!-- SAMPLE_CASES:3 -->

## 4. DAL, repositories, and transactions

| ID | Case | Library surface | Verifiable outcome |
|---|---|---|---|
<!-- SAMPLE_CASES:4 -->

## 5. Authentication, identity, and authorization

| ID | Case | Library surface | Verifiable outcome |
|---|---|---|---|
<!-- SAMPLE_CASES:5 -->

## 6. Events, jobs, email, and notifications

| ID | Case | Library surface | Verifiable outcome |
|---|---|---|---|
<!-- SAMPLE_CASES:6 -->

## 7. Localization, configuration, and HTTP infrastructure

| ID | Case | Library surface | Verifiable outcome |
|---|---|---|---|
<!-- SAMPLE_CASES:7 -->

## 8. Frontend HTTP, forms, and modals

| ID | Case | Library surface | Verifiable outcome |
|---|---|---|---|
<!-- SAMPLE_CASES:8 -->

## 9. Frontend collections, routing, authentication, and interaction

| ID | Case | Library surface | Verifiable outcome |
|---|---|---|---|
<!-- SAMPLE_CASES:9 -->

## 10. Utilities, SEO, SSR, and observability

| ID | Case | Library surface | Verifiable outcome |
|---|---|---|---|
<!-- SAMPLE_CASES:10 -->

## 11. Common helpers and caching

| ID | Case | Library surface | Verifiable outcome |
|---|---|---|---|
<!-- SAMPLE_CASES:11 -->

## 12. Test helpers

| ID | Case | Library surface | Verifiable outcome |
|---|---|---|---|
<!-- SAMPLE_CASES:12 -->

## 13. Media

| ID | Case | Library surface | Verifiable outcome |
|---|---|---|---|
<!-- SAMPLE_CASES:13 -->

## 14. Application services and unit of work

| ID | Case | Library surface | Verifiable outcome |
|---|---|---|---|
<!-- SAMPLE_CASES:14 -->

## 15. Audit: helpers, extensions, and transactional boundaries

| ID | Case | Library surface | Verifiable outcome |
|---|---|---|---|
<!-- SAMPLE_CASES:15 -->

## 16. Authorization implementation patterns

| ID | Case | Library surface | Verifiable outcome |
|---|---|---|---|
<!-- SAMPLE_CASES:16 -->

## 17. Consumer repository foundation

| ID | Case | Library surface | Verifiable outcome |
|---|---|---|---|
<!-- SAMPLE_CASES:17 -->

## Coverage matrix

| Library area | Cases | Count |
|---|---:|---:|
| CRUD, controllers, services, and models | SPM-001–015 | 15 |
| Collections, filters, expressions, and projections | SPM-016–035 | 20 |
| Full, partial, and bulk mutations and validation | SPM-036–050 | 15 |
| DAL, repositories, SQL, EF, and transactions | SPM-051–060 | 10 |
| Authentication, identity, claims, and policies | SPM-061–075 | 15 |
| Events, Hangfire, email, and notifications | SPM-076–090 | 15 |
| Localization, options, middleware, and OpenAPI | SPM-091–105 | 15 |
| Frontend HTTP, forms, and modals | SPM-106–125, SPM-215 | 21 |
| Frontend collections, routing, authentication, and interaction | SPM-126–140 | 15 |
| Utilities, SEO, SSR, and observability | SPM-141–161 | 21 |
| Common helpers and caching | SPM-162–172 | 11 |
| Test helpers | SPM-173–176 | 4 |
| Media | SPM-177–188 | 12 |
| Application services and unit of work | SPM-189–200 | 12 |
| Helpers, extensions, and transactional boundaries | SPM-201–209 | 9 |
| Authorization implementation patterns | SPM-210–214 | 5 |
| Consumer repository foundation | SPM-216–217 | 2 |
| **Total** | **217 cases** | **217** |

## Identified gaps and risks

1. Previously partial authentication, HTTP, form, SEO and router, state, and observability exports now have executable playground cases and evidence paths.
2. SPM-093 checks every translation resource family in the sample for missing and extra keys; module keys are also validated separately for dash-case.
3. German shared and annotation resources now also live under the NewHeap-configured `Resources` path; SPM-092 tests both lookups.
4. SPM-177 through SPM-188 demonstrate the complete media structure: SQL metadata, local binaries, optional S3 configuration, scoped authorization, thumbnails, HTTP, and events.
5. SPM-189 through SPM-200 provide a live Transactions workbench that visualizes a service-owned scope, outbox publication, rollback, and verification outside the transaction.
6. Three gaps remain visible: OneOf OpenAPI schema construction and two SSR or server-interceptor cases without an Angular server host.
7. The library audit added SPM-201 through SPM-207 for the boundaries of larger flows: direct ChunkAsync guards and cancellation, server-side semaphores, safe formatting, Identity result conversion, and JWT validation configuration.
8. SPM-210 through SPM-214 make the authorization hierarchy and extension points executable: application roles, active-division roles, a consumer-specific resource permission, an authentication-service override, and request-time claim hydration.
9. SPM-112 and SPM-215 show the recommended opt-ins explicitly in the sample configuration. The library defaults deliberately remain disabled for backward compatibility with existing applications.
10. SPM-216 keeps shared .NET build settings and direct NuGet versions centralized inside `src/Back-end` so generated consumer projects start from the same backend-wide contract.
11. SPM-217 proves that the versioned consumer bootstrap creates the standard layout and that post-bootstrap inspection rejects root-level workspace drift before feature work continues.

## Follow-up for library gaps

The sample backlog is empty. Further coverage first requires OneOf schema work for
SPM-102 and an Angular server host for SPM-113 and SPM-160. These cases deliberately
remain gaps until their underlying behavior can be demonstrated end to end.
