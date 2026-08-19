# NewHeap surface-to-case matrix

This is the completion criterion for SampleProjectManagement: every author-facing NewHeap concept has a concrete case, an entry point in the sample, and an evidence path in the generated catalog. A public runtime-plumbing type is covered through its intended registration point, never by locally recreating or directly instantiating a library implementation.

| Library surface | Author-facing concepts | Cases | Concrete sample entry |
|---|---|---|---|
| NewHeap.Platform.Common | TaskResult, CRUD/bulk contracts, filters, projections, validation, utilities, localization and Hangfire helpers | SPM-001–050, SPM-076–083, SPM-141–169, SPM-201–207 | ProjectService, collection and utility playgrounds, executable tests |
| NewHeap.Platform.AspNet.Common: DAL and services | repository, composite service, partial/bulk mutation, SQL, transaction ownership and db logging | SPM-051–052, SPM-054–060, SPM-189–200 | concrete ProjectService and ProjectCompositeService |
| NewHeap.Platform.AspNet.Common: authentication | password, Microsoft OAuth, picker, refresh, logout, impersonation, account, scoped policies, authentication-service overrides and request-time claim transformation | SPM-061–075, SPM-210–214 | Program authentication builder, SampleAuthenticationService, AuthorizationSamplesController and auth playground |
| NewHeap.Platform.AspNet.Common: notifications | notification creation, deliveries, dispatcher and frontend state | SPM-084–090 | OperationsSampleService and notification playground |
| NewHeap.Platform.AspNet.Common: application infrastructure | configuration, startup builder, localization, trace, error, OneOf, query binding and invariant forms | SPM-091–105, SPM-209 | Program, SampleStartupConfiguration and LibrarySamplesController |
| NewHeap.Platform.AspNet.Caching | registration, cache key, hit and invalidation | SPM-170–172 | Program and platform playground |
| NewHeap.Platform.Events.Cap | event configuration, publisher, typed/custom consumers, outbox, retries, delivery group and rollback | SPM-076–080, SPM-193–200, SPM-208 | Program, ProjectEvents and transaction playground |
| NewHeap media projects | composition root, SQL structure, filesystem/S3 storage, authorization, folders/files, tags, search, thumbnail, HTTP and events | SPM-177–188 | Program, media controller/service and media playground |
| NewHeap reusable test-helper packages | testing contexts, DbContext context, TaskResult assertions and NSubstitute predicates | SPM-173–176 | consumer-owned core tests that reference the packable `.Test` helpers; library self-tests stay in internal `.Tests` projects |
| nh-common: HTTP and API | request/response options, encoding, authentication/active-division/deduplication interceptors and API service lifecycle | SPM-106–114, SPM-126–130 | shared ProjectApiService and collection/platform playgrounds |
| nh-common: forms and modals | value accessors, dropdown/page size/search, deferred lazy loading, form errors, validation, mutate lifecycle, loading/confirm/edit modal | SPM-115–125, SPM-148, SPM-154, SPM-215 | project edit modal, utility, interaction and platform playgrounds |
| nh-common: routing, auth and notifications | guards, application/division/resource pipes, routes, page state, context menu and notification component | SPM-061–075, SPM-089–090, SPM-131–140, SPM-153, SPM-210–214 | auth/notification playgrounds and route configuration |
| nh-common: utilities and browser services | utilities, arrays/observables, mutex, cookies, connection, head/meta/JSON-LD, Sentry and browser translations | SPM-091–095, SPM-141–161 | utility and platform playgrounds |

## Runtime plumbing

Some public types exist because the framework needs to resolve them through DI, EF Core or CAP. They are still covered, but their correct sample is the composition-root configuration and observable behaviour rather than direct construction:

- CapTransactionScope and CapEFDbTransaction: SPM-193, SPM-195 and SPM-200.
- NhConsumerSelector: SPM-078 and SPM-208.
- JsonQueryModelBinder and its provider: SPM-103 through FromQuery collection endpoints.
- NhDbLogAdditionalDataProcessingService: SPM-060 through WithDbLogService registration; it is a hosted, configuration-owned processor.
- generated identity, notification, media and EF entity models: the relevant create/read/update and delivery cases above.

## Explicit library gaps

The three remaining gaps are deliberately visible: SPM-102, SPM-113 and SPM-160. They are the only concepts in this matrix that are not executable today because the underlying behaviour or sample host is still missing.
