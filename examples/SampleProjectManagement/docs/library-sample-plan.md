# NewHeap library sample plan

This is the coverage backlog for `SampleProjectManagement`. Its purpose is to make every public part of the NewHeap backend and frontend libraries discoverable through a concrete, executable sample. Related contracts and implementations may be covered together in one vertical case.

## Research

The inventory combines the public types from `NewHeap.Platform.Common` and `NewHeap.Platform.AspNet.Common`, every export from `nh-common`, and the implemented SampleProjectManagement applications. A heuristic scan found approximately 280 public backend declarations and 229 frontend exports. Selectors, pipes, extension methods, DI, and reflection are not always discoverable by type name. The sample therefore describes only demonstrable library behavior and contains no external implementation provenance.

## Definition of done

A case becomes a sample only when it is executable from Management or Workspace, uses real NewHeap types, explains its intent and request or response, and has a focused test or reproducible check. Backend modules include controller, service, entity, view model, mutate model, a NewHeap mapping profile, repository, `DbSet`, and relationships where relevant, with authorization and no library-owned DAL migrations. Mutate models contain no audit fields. Mutating modal content uses `NhModalMutateBaseComponent`; other dynamic modal content may use `NhModalComponentImpl`. Large modals use `modalClasses: 'large'`. Pages, collections, and modal content derived from a NewHeap base use the `appOn...` extension points and do not override Angular hooks owned by the base. Documentation and AI instructions are English. Every executable UI has a complete English translation; additional languages use matching lowercase dash-case keys under the module object.

## Library sources

- `NH-BE`: [NewHeap.Platform.Common](../../../src/Back-end/Libraries/NewHeap.Platform.Common) and [NewHeap.Platform.AspNet.Common](../../../src/Back-end/Libraries/NewHeap.Platform.AspNet.Common)
- `NH-FE`: [public Angular API](../../../src/Front-end/projects/nh-common/src/public-api.ts)
- `CURRENT`: [current sample catalog](sample-catalog.md)

The complete public-surface mapping and intended sample entry points are documented in [newheap-surface-to-case-matrix.md](newheap-surface-to-case-matrix.md).

## 1. Domain, CRUD, and models

| ID | Case | Library surface | Verifiable outcome |
|---|---|---|---|
| SPM-001 | Retrieve projects | protected base controller, collection response | The register displays paged projects. |
| SPM-002 | Project by ID | `GetById`, `TaskResult<T>` | The detail page displays one project and a clean 404 response. |
| SPM-003 | Create a project | `Create`, `BaseDbEntityService` | The create modal creates and returns a project. |
| SPM-004 | Full update | `Update`, `CRUDActionType.Update` | The edit flow saves every mutable field. |
| SPM-005 | Delete | `Delete`, delete validation | Confirmation deletes the project and refreshes the collection. |
| SPM-006 | Protected CRUD | `DbEntityProtectedNhBaseController` | Requests without the claim receive 403; requests with the claim can perform CRUD. |
| SPM-007 | Public read API | `PublicNhBaseController` | Read operations work without login while mutations remain protected. |
| SPM-008 | Composite detail | composite controller/service | Project, members, and labels are returned in one response. |
| SPM-009 | View model contract | `[Filterable]` on `Id`, audit fields | ID filtering works and the OpenAPI contract is correct. |
| SPM-010 | Mutate model contract | create/update mutate model | The contract contains no audit fields and prevents overposting. |
| SPM-011 | NewHeap mapping | profile, null-safe `MapFrom`, nested, generic and non-generic collection mapping, dictionary mapping, dictionary-shaped JSON objects, inherited base maps, duplicate profile composition, ignored members, DI resolvers, converters, construction, mapping actions, configuration validation, `MapOnlyIfChanged` | Entity, view, mutate, nested, collection, dictionary and value-object mappings use `NewHeap.Platform.Mapping`; `IncludeBase` reuses base member configuration and mapping actions for derived destinations while allowing explicit derived overrides; duplicate maps preserve AutoMapper 14's last-registration runtime behavior while explicit configuration validation reports every contributing profile; generic collection and key/value entries are used even when a source exposes a different non-generic enumeration path, standalone key/value pairs convert and validate per member, set interfaces materialize as sets, concrete read-only collections and dictionaries are wrapped from mutable results, incompatible existing enumerable members are replaced, dictionary key and value conversions validate independently, and supported non-generic list destinations follow AutoMapper 14 materialization and reuse behavior; explicit `MapFrom` expressions tolerate unloaded nullable navigations like AutoMapper 14; ignored members are not read; DI-backed resolvers and actions enrich results; construction and conversion remain centralized; configuration validation fails on duplicate, unmapped or incompatible members; compatible existing navigation references remain stable during mutate mapping; and recursion depth is bounded. |
| SPM-012 | Changed properties | `OnUpdateGetChangedProperties` | The log contains only actual changes. |
| SPM-013 | Module wiring | DbSet, relationships, repository, DI | The module starts and its relationships load. |
| SPM-014 | Audit fields | base entity timestamps | Create and update timestamps are correct. |
| SPM-015 | Uniform result handling | `TaskResult`, item, disposable result | The UI shows only confirmed success and distinguishes warnings, errors, and local simulation. |

## 2. Collections, filtering, and projections

| ID | Case | Library surface | Verifiable outcome |
|---|---|---|---|
| SPM-016 | Free-text search | `[Searchable]`, searchable request | Changing the name or description changes the result and count. |
| SPM-017 | Equality filter | fluent `.equals()` + `[Filterable]` | The preferred status filter is visible in the request. |
| SPM-018 | AND/OR filter tree | fluent `.and()` / `.or()` | An explicitly nested status OR combines with the remaining AND filters. |
| SPM-019 | Text operators | fluent `.like()` | The playground compares contains, starts-with, and ends-with behavior. |
| SPM-020 | Date range | `.greaterThanOrEqual()` + `.lessThanOrEqual()` | The deadline range includes both boundaries. |
| SPM-021 | Null filter | fluent `.equals(key, null)` | The no-deadline option finds only null values. |
| SPM-022 | Multiple-value filter | fluent `.isIn()` | Multiple statuses can be combined. |
| SPM-023 | Single-column sorting | `.orderAsc()` / `.orderDesc()` + `[Orderable]` | The column switches between ascending and descending server-side sorting. |
| SPM-024 | Multi-column sorting | chained fluent order shortcuts | Status and name sorting remains stable. |
| SPM-025 | Paging | collection request options | Page and page-size values match the metadata. |
| SPM-026 | Simple collection | simple request/result | The dropdown loads a lightweight collection. |
| SPM-027 | User collection | user collection request | My projects uses the current user context. |
| SPM-028 | Explicit projection | projection extensions | SQL selects only the list columns. |
| SPM-029 | Short projection | short-projection extensions | The dropdown retrieves only ID and name. |
| SPM-030 | NhProjection builder | builder/source/definition | Calculated fields work without IncludeAll. |
| SPM-031 | HTTP processing | `IHttpCollectionProcessingService` | Filtering, ordering, paging, and projection work together. |
| SPM-032 | In-memory processing | `CollectionProcessingService` | The same request runs against a test collection. |
| SPM-033 | Expression resolver | collection expression resolver | The UI displays the generated expression. |
| SPM-034 | Attribute protection | filter/search/order attributes | A disallowed field returns a safe error. |
| SPM-035 | Preserve filter state | collection base, URL/storage | Refresh and browser back preserve state. |

## 3. Mutations, partial and bulk operations, and validation

| ID | Case | Library surface | Verifiable outcome |
|---|---|---|---|
| SPM-036 | Create and edit modal | modal service, modal mutate base | One modal handles both create and edit operations. |
| SPM-037 | End-to-end single-field JSON partial update | `NhApiService.patch`, `NhBaseApiService.updatePartial`, `DoUpdatePartial`, `UpdatePartialAsync`, `PreparePartialUpdateMutateModelAsync`, setters | The frontend sends only the selected property with PATCH; the backend normalizes and validates it while omitted values remain unchanged, and a rejection preserves the previous UI value. |
| SPM-038 | Multi-field JSON partial update and custom workflow | `DoUpdatePartial`, `TryApplyPartialUpdate`, multiple mapped setters, custom service workflows | A custom planning route loads an existing mutate model, changes deadline and description atomically, preserves omitted properties and then runs its domain workflow. |
| SPM-039 | Bulk partial update | `BulkAsync`, bulk mutate | The selection receives one status. |
| SPM-040 | Bulk rollback | transactional bulk options | One failure rolls back the entire operation. |
| SPM-041 | Continue on error | per-item bulk result | Valid items succeed independently. |
| SPM-042 | Bulk create | bulk create mutation | The CSV preview creates tasks per row. |
| SPM-043 | Bulk full update | bulk update mutation | Complete mutate models are processed in a transaction. |
| SPM-044 | Bulk delete | bulk delete mutation | Confirmation and dependency errors are handled per item. |
| SPM-045 | Bulk result | `BulkCRUDResultModel` | The UI groups successes and failures and never reports transport failures as success. |
| SPM-046 | Create validation | validation service/model | A duplicate code produces a field-level message. |
| SPM-047 | Update validation | changed properties + validation | The rule runs only when the relevant field changes. |
| SPM-048 | Delete validation | `CRUDActionType.Delete` | Open tasks block deletion. |
| SPM-049 | Validation attributes | required/greater/less attributes | English and Dutch display the same rule. |
| SPM-050 | ModelState to form | extensions/response types | The error appears on the correct control. |

## 4. DAL, repositories, and transactions

| ID | Case | Library surface | Verifiable outcome |
|---|---|---|---|
| SPM-051 | Generic repository | `IRepository<T>`, `Repository<T>` | The service queries through a DI-registered repository. |
| SPM-052 | Load relationships | repository/queryable extensions | The detail loads without an N+1 query. |
| SPM-053 | Provider-native bulk upsert | `ExecuteUpsertAsync` | Project imports atomically upsert roots and explicitly selected one-to-many tasks, retain omitted children, and reject populated nested dependencies before changing stored data. |
| SPM-054 | Explicit transaction | `ITransaction`, transaction scope | Project and task commit or roll back together. |
| SPM-055 | Safe raw SQL | raw/safe formattable string | Injection input remains parameterized. |
| SPM-056 | Repository SQL | SQL extensions | A read-only statistics query executes through the repository. |
| SPM-057 | EF bulk update | EF batch extensions | Overdue tasks are updated with a set-based operation. |
| SPM-058 | EF bulk delete | EF batch extensions | Drafts are deleted with a set-based operation that returns a count. |
| SPM-059 | Query expressions | expression extensions/utils | Optional predicates remain server-side. |
| SPM-060 | Database log data | log additional-data processor | The audit log contains relational context. |
| SPM-218 | Read-only database diagnostics | `newheap-db` JSON contract plus consumer-owned governed schema, indexes and query MCP tools | Developers and agents inspect selectable live schema and permission-filtered named outgoing and incoming relationships with ordered column pairs and validation status, then optionally make at most one focused index lookup when a positioned column or expression key immediately improves the predicate or ordering; otherwise they continue with a bounded parameterized diagnostic instead of spending calls on index analysis. Structured MCP input and the direct CLI `--request-file` option keep serialized JSON out of Windows process arguments so transport limits never change the intended candidate set. The executable sample MCP fixes profile, capability, a sixteen-call budget and ceilings server-side, requires an explicit requested row count, rejects excessive requests before execution, and fails without partial data when actual results exceed the accepted limit, while SQL Server and PostgreSQL enforce dedicated read-only credentials and return only allowlisted provider failure classifications. |

## 5. Authentication, identity, and authorization

| ID | Case | Library surface | Verifiable outcome |
|---|---|---|---|
| SPM-061 | Password login | username/password handler | Login restores the original route. |
| SPM-062 | Microsoft OAuth | handlers/service/models | The URL, callback, and failure path work. |
| SPM-063 | Choose a login method | method picker service | Email or domain selection chooses the correct flow. |
| SPM-064 | Refresh token | refresh handler | The access token is refreshed exactly once. |
| SPM-065 | Session expiration | expiration information | The live countdown expires, token state is cleared, and both apps open login with a reason and return URL. |
| SPM-066 | Logout | logout handler | Local tokens and state are removed even when server logout fails, and the protected route closes. |
| SPM-067 | Impersonation | handler/models | A visible impersonation banner is displayed. |
| SPM-068 | Revert impersonation | revert handler/models | The original user is restored. |
| SPM-069 | Account information | account endpoint/models | User, claims, division, and roles load. |
| SPM-070 | Claim authorization | authorize attribute | The endpoint returns 403 without the claim. |
| SPM-071 | Any claim | match-one attribute | An editor or administrator may update. |
| SPM-072 | Active division | requirement/handler | A different division is inaccessible. |
| SPM-073 | Switch division | change-active-division model | Claims, data, and context refresh. |
| SPM-074 | Frontend guards | auth/permission guards | Routes and actions follow claims. |
| SPM-075 | Password management | recover/reset/change models | All three account flows are complete. |

## 6. Events, jobs, email, and notifications

| ID | Case | Library surface | Verifiable outcome |
|---|---|---|---|
| SPM-076 | Publish an event | `INhEventPublisher` | Project-created is published with a correlation ID. |
| SPM-077 | Consume an event | `INhEventConsumer<T>` | The consumer creates an audit activity. |
| SPM-078 | Custom topic | custom-topic consumer | A priority event is sent to an explicit topic. |
| SPM-079 | Retry and idempotency | event configuration | A retry does not create a duplicate activity. |
| SPM-080 | Event bus registration | event configuration builder | The Aspire health check displays the connection. |
| SPM-081 | Enqueue a job | `NhHangfireUtil` | Recalculation runs outside the HTTP request. |
| SPM-082 | Recurring job | Hangfire extensions | The overdue job is visible and can be started. |
| SPM-083 | Durable background operation with fan-out and nested progress | `WithBackgroundOperations`, `INhBackgroundOperationHandler<T>`, `TaskResult`, `NhBackgroundOperationRetryResult`, fan-out/fan-in, durable leases, scoped polling, and scoped SignalR | A division-exclusive parent durably fans out project work, releases its worker while children execute concurrently, propagates expected batch, checkpoint, step, and fan-in outcomes through TaskResult, reschedules internal lock contention without consuming handler retries, advances a contended final-child wake-up to the next dispatcher interval, aggregates nested progress, protects unprojected notification milestones during event retention, starts under strict EF Core warning policies, and remains isolated to the authenticated user and accessible active division through notifications, SignalR, and polling. |
| SPM-084 | Simple email | mail service/settings | A test email uses the correct sender. |
| SPM-085 | Razor email template | Razor view service | A localized assignment email is rendered. |
| SPM-086 | Create and summarize user notifications | `INhUserNotificationService` and `NhUserNotificationService` | Assignment creates the correct message, and the overview remains query-safe under strict EF Core warning policies on SQL Server and PostgreSQL. |
| SPM-087 | Delivery channels | notification dispatcher workers and per-channel concurrency | A channel opts into parallel workers while unconfigured channels remain serial and deliveries are claimed only when worker capacity is available. |
| SPM-088 | Email dispatcher | email notification dispatcher | Sent and failed states include error details. |
| SPM-089 | Read and unread | notification controller/models | The badge responds to mark-as-read. |
| SPM-090 | Notification component | abstract component + FE service | The message, refresh, and target route work. |
| SPM-229 | Durable external-signal suspension and wake-up | `INhBackgroundOperationSuspensionContext`, `INhBackgroundOperationSignalService`, `WaitingForSignal`, typed checkpoint signals, durable expiry, duplicate detection, cancellation, and dispatcher wake-up | A running operation atomically suspends its attempt and releases the worker, accepts one owner-bound typed signal, treats an identical duplicate idempotently, rejects a conflicting signal, resumes as a new fenced attempt, preserves completed idempotent work, and remains cancellable or expiry-wakeable without polling. |

## 7. Localization, configuration, and HTTP infrastructure

| ID | Case | Library surface | Verifiable outcome |
|---|---|---|---|
| SPM-091 | Composite localizer | composite string localizer | Domain and shared fallback behavior is predictable. |
| SPM-092 | Validation translations | shared annotation resources | The rule is available in Dutch, English, and German. |
| SPM-093 | Resource completeness | localizer factory/options | The test reports missing and extra keys. |
| SPM-094 | Module translations | browser translate loader | project.<dash-case-key> resolves correctly. |
| SPM-095 | Language and culture | language map/globalization util | nl selects nl-NL consistently. |
| SPM-096 | Common options and configuration overrides | common/aspnet builders and configuration providers | The app and automation use the same appsettings with host-specific environment and CLI overrides. |
| SPM-097 | Startup order | configurators/app builder | The minimum registration starts correctly. |
| SPM-098 | Trace identifier | trace middleware/extensions | The response, log, and event share a trace ID. |
| SPM-099 | No-follow | no-follow middleware | The expected robot header is present. |
| SPM-100 | Exception handling | handler + bad-request model | A safe response includes a trace ID. |
| SPM-101 | OneOf JSON | converter | Success and error variants serialize consistently. |
| SPM-102 | OneOf OpenAPI | schema transformer | Swagger displays both variants. |
| SPM-103 | JSON query binding | binder/provider | A complex filter binds from the query string. |
| SPM-104 | Invariant form values | value-provider factory | Decimal and date values behave the same in English and Dutch. |
| SPM-105 | Backend observability | ILogger/OpenTelemetry/activity scope | Structured completion and handled-failure logs share timing, sample context, and trace context. |

## 8. Frontend HTTP, forms, and modals

| ID | Case | Library surface | Verifiable outcome |
|---|---|---|---|
| SPM-106 | Typed GET | API service/request options | A typed detail request uses the uniform error flow. |
| SPM-107 | POST, PUT, and DELETE | API mutation methods | All mutations use one loading and TaskResult flow. |
| SPM-108 | Binary response | array-buffer options | Bytes are returned without JSON conversion. |
| SPM-109 | Text response | text request options | The plain-text preview is correct. |
| SPM-110 | Download | download options, HTTP util | Filename and content type are preserved. |
| SPM-111 | Query encoding | encode interceptor | Special characters are encoded exactly once. |
| SPM-112 | GET deduplication | deduplication interceptor and explicit sample configuration | Two concurrent identical GET requests share one network request; a later call starts a new request after completion. |
| SPM-113 | Server HTTP | server interceptor | SSR uses the internal base URL. |
| SPM-114 | Base API service | `NhBaseApiService` | The project service contains only domain code. |
| SPM-115 | Edit modal | modal service/options/base | Create and edit work from both list and detail pages. |
| SPM-116 | Large modal | `modalClasses: 'large'` | The large class is demonstrably active. |
| SPM-117 | Confirmation modal | confirm component | Delete respects cancellation. |
| SPM-118 | Loading modal | loading component | Duplicate submission is prevented and the modal always closes. |
| SPM-119 | Modal lifecycle | ref/result/events/directives + `appOn...` | appOnInit initializes content; the parent receives events and appOnDestroy cleans up. |
| SPM-120 | Mutate base state | mutate base components | Dirty, saving, error, and success states are visible. |
| SPM-121 | Single select | enum optionbuilder + dropdown settings | The translated status choice preserves the API enum contract. |
| SPM-122 | Multi-select | enum optionbuilder + defaults/texts | Multiple translated statuses can be selected. |
| SPM-123 | Form error | error-message component | Client and server errors are not duplicated. |
| SPM-124 | Server form validation | validator + result models | A nested error appears on the correct control. |
| SPM-125 | TaskResult validator | form validator | General and field errors remain separate. |
| SPM-215 | Deferred lazy dropdown | dropdown settings and explicit sample configuration | An existing selection loads immediately; the full collection loads only when opened, and identical in-flight selected-value lookups share one request. |

## 9. Frontend collections, routing, authentication, and interaction

| ID | Case | Library surface | Verifiable outcome |
|---|---|---|---|
| SPM-126 | Collection base | collection base component + `appOn...` | appOnInit runs before the initial beforeLoad, onLoad, and afterLoad cycle. |
| SPM-127 | Search debounce | search input/directive | Rapid typing produces one request. |
| SPM-128 | Page size | page-size component | Changing page size resets the page and preserves the preference. |
| SPM-129 | Page base | page base component + `appOn...` | Title, breadcrumb, and route loading use the base lifecycle; independent work is detached with observed errors while required ordering remains awaitable. |
| SPM-130 | Dirty navigation | cancel guard/interface | Confirmation respects cancellation. |
| SPM-131 | Register routes | routes/register/setup | Lazy routes include metadata. |
| SPM-132 | Router link | directive/model | Context and modifier keys are preserved. |
| SPM-133 | Preload URL | preload model | The chunk or call is demonstrably preloaded. |
| SPM-134 | Preconnect URL | preconnect model | The document head contains the preconnect entry. |
| SPM-135 | Authentication guard | authenticated guard | The return URL is restored after login. |
| SPM-136 | Single permission | one-permission guard/pipe | One claim allows the action. |
| SPM-137 | All permissions | all-permissions guard/pipe | Both claims are required. |
| SPM-138 | Active division in the frontend | division guard/pipes | Route and content change with the active division. |
| SPM-139 | Authorization pipes | claim/permission/auth pipes | The playground displays true and false results. |
| SPM-140 | Context menu | service/models/events | Opening and closing are accessible. |

## 10. Utilities, SEO, SSR, and observability

| ID | Case | Library surface | Verifiable outcome |
|---|---|---|---|
| SPM-141 | String utilities | string util/uppercase | Edge cases have live output. |
| SPM-142 | HTTP utilities | HTTP util | Query and download input and output are visible. |
| SPM-143 | Encoding | encoding util/pipes | Unicode, URL, and Base64 values round-trip. |
| SPM-144 | Common utilities | enum/nameof/guid/groupBy/defined | The existing numeric reverse mappings and string enum values are visible. |
| SPM-145 | Array extensions | array prototype extensions | Operations and mutation effects are visible. |
| SPM-146 | Observable extensions | observable prototype extensions | The lifecycle completes without subscription leaks. |
| SPM-147 | Async mutex | async lock/mutex | Duplicate save attempts produce one mutation. |
| SPM-148 | Custom form control | value accessor/provider | Reactive forms and disabled state work. |
| SPM-149 | Cookies | cookie service | Consent can be set, read, and deleted. |
| SPM-150 | Internet status | connection service | The offline banner and reconnect retry work. |
| SPM-151 | Title, metadata, and head | services/directive | Title, description, and canonical URL change. |
| SPM-152 | JSON-LD | service/component/keys | Valid structured data appears in the document head. |
| SPM-153 | Breadcrumb and sitemap | page/app/router metadata | Both derive from the same route configuration. |
| SPM-154 | Loader and error boundary | components/handlers | Loading, empty, and error states are separate. |
| SPM-155 | Safe content | safe HTML/style/URL pipes | Only trusted content is exposed. |
| SPM-156 | Date and primitive pipes | date/boolean pipes | Timezone, null, and boolean values are localized. |
| SPM-157 | Debounced events | input/click directives | Timing and teardown are visible. |
| SPM-158 | Application and page state | app/page/config services | Runtime configuration changes without a reload. |
| SPM-159 | Provider-neutral frontend error handling | NH_ERROR_HANDLERS/ErrorHandler provider chain | Application errors reach registered providers without requiring a vendor SDK or storing raw messages. |
| SPM-160 | SSR stack | server/node-fetch/translate loaders | SSR is localized and makes no browser-only call. |
| SPM-161 | EF Core chunks | `IQueryable.ChunkAsync` | An ordered query returns bounded asynchronous batches. |

## 11. Common helpers and caching

| ID | Case | Library surface | Verifiable outcome |
|---|---|---|---|
| SPM-162 | Conditional queries | `WhereIf` / `IncludeIf` | Disabled conditions do not change the query; enabled conditions do. |
| SPM-163 | Combine expressions | `True` / `False` / `And` / `Or` | Dynamically composed predicates return the expected matches. |
| SPM-164 | Dictionary get-or-add | synchronous and asynchronous factories | A factory runs at most once per key. |
| SPM-165 | In-memory paging | `PageSkipTake` | List and IQueryable normalize page and page size identically. |
| SPM-166 | String and attribute boundaries | string/attribute extensions | Length, HTML, JSON, boolean, and StringLength behavior have executable tests. |
| SPM-167 | Type and reflection | type extensions | Instantiation, generics, simple types, and property traversal are visible. |
| SPM-168 | EF model metadata | `IModel.Table` / `Column` | Schema, table, and column names are quoted correctly. |
| SPM-169 | Hash, culture, and timing | Hash/Globalization/Stopwatch utils | Hashes are stable, culture is scoped, and the stopwatch stops. |
| SPM-170 | Default memory cache | `AddNewHeapPlatformCachingDefault` | The Aspire API registers FusionCache with explicit defaults. |
| SPM-171 | Cache key and hit | `NhCacheKey` / `GetOrSetAsync` | Two reads share a key and generation timestamp. |
| SPM-172 | Cache invalidation | `IFusionCache.RemoveAsync` | Authorized invalidation forces the next read to be fresh. |

## 12. Test helpers

| ID | Case | Library surface | Verifiable outcome |
|---|---|---|---|
| SPM-173 | Test service context | `NhTestingContext` | A consumer test uses the packable, xUnit-version-neutral NH helper for a validated DI provider, scopes, and asynchronous disposal under a consumer-owned xUnit version; library regression tests remain in a separate non-packable test project. |
| SPM-174 | DbContext test context | `NhDbContextTestingContext` | A consumer test uses the ASP.NET NH helper to register an in-memory DbContext and repositories automatically without publishing library-owned tests. |
| SPM-175 | TaskResult assertions | test assertion extensions | A consumer test reads success and error results through the reusable NH assertion extensions. |
| SPM-176 | Predicate substitutes | NSubstitute test extensions | A consumer test uses the reusable NH NSubstitute extensions to evaluate real expressions against sample data. |

## 13. Media

| ID | Case | Library surface | Verifiable outcome |
|---|---|---|---|
| SPM-177 | Media composition root | `AddNhMedia` / context | Storage, structure, and authorization modules are registered together. |
| SPM-178 | File-system media storage | `UseFileSystemMediaStorage` | Upload and read operations use a disposable local storage folder. |
| SPM-179 | PostgreSQL file structure | PostgreSQL structure provider | Folders, files, and relationships are stored through the independent PostgreSQL provider package without a SQL Server package dependency or library-owned sample migrations. |
| SPM-180 | S3 media storage | S3 settings/provider | Configuration validates bucket, region, and credentials without logging secrets. |
| SPM-181 | Media authorization | authorization module/context | Read, mutate, and delete operations respect the active user and division. |
| SPM-182 | Folder lifecycle | folder create/update/delete | Project folders can be created, renamed, and deleted safely. |
| SPM-183 | File upload and download | media service + HTTP | File content, content type, and download name remain intact. |
| SPM-184 | Tags and properties | tags/properties/localization | Metadata and translations round-trip through the API and UI. |
| SPM-185 | Media search and sorting | search/file-get options | Query, paging, and sorting return stable results. |
| SPM-186 | Thumbnails | thumbnail service/events | Upload generates a thumbnail and cleanup removes derivatives. |
| SPM-187 | Media HTTP surface | endpoint mapper/filter | The route group injects context and validates upload requests. |
| SPM-188 | Media events | folder/file event handlers | Create, update, and delete operations publish the expected events. |

## 14. Application services and unit of work

| ID | Case | Library surface | Verifiable outcome |
|---|---|---|---|
| SPM-189 | Thin controller | protected base controller + concrete service | The controller handles only HTTP, authorization, ModelState, and response mapping. |
| SPM-190 | Business rules in the service | validation hooks + repository queries | Normalization, unique keys, and delete conditions work outside HTTP and in bulk operations. |
| SPM-191 | Service-owned transaction | `StartOrGetTransactionScopeAsync` | The application service opens the outer unit of work and commits exactly once. |
| SPM-192 | Nested partial and bulk operations | transaction scope ownership + `UseTransaction = false` | Library mutations share the outer transaction and cannot commit it prematurely. |
| SPM-193 | Event before commit | `INhEventPublisher` + CAP outbox | The project event is published after save but before commit in the same transaction. |
| SPM-194 | Failure without commit | `TaskResult` + uncommitted scope dispose | A validation failure publishes no event and starts no commit. |
| SPM-195 | SQL outbox and broker | CAP EF storage + Aspire RabbitMQ | The domain write and outbox record use SQL; RabbitMQ receives the message only after commit. |
| SPM-196 | Publication failure rolls back | `PublishAsync` failure + `RollbackAsync` | A publication exception causes an explicit rollback and never a commit. |
| SPM-197 | Only the owner commits | `NhDbTransactionScope.IsMyTransaction` | A nested scope cannot commit or roll back the transaction started by the outer service. |
| SPM-198 | Partially successful bulk operation | `ContinueOnError` + outer transaction | Only successful items are stored and counted; one summary event precedes one commit. |
| SPM-199 | Idempotent consumer | event ID deduplication | A redelivered event ID is processed once by the concrete consumer log. |
| SPM-200 | Publication followed by rollback | SQL write + CAP outbox + deliberate rollback | The project and outbox event both remain invisible when the outer service rolls back. |

## 15. Audit: helpers, extensions, and transactional boundaries

| ID | Case | Library surface | Verifiable outcome |
|---|---|---|---|
| SPM-201 | Chunk validity | `IQueryable.ChunkAsync` guard | A batch size of zero fails before a query executes. |
| SPM-202 | Chunk cancellation | `IQueryable.ChunkAsync` cancellation token | A cancelled caller stops before the first batch. |
| SPM-203 | Server-side semaphore | `SemaphoreLocker` / `SemaphoreSlimAsync` | One and two permits respectively limit parallel critical sections. |
| SPM-204 | Semaphore failure recovery | semaphore `finally` release | An exception does not leak a permit; subsequent work can continue. |
| SPM-205 | Safe log formatting | `SafeFormattableStringFactory` | Broken ToString values or format strings do not break logging. |
| SPM-206 | Identity to result | `IdentityResult.ToTaskResult` | The identity error code and description remain visible as a TaskResult error. |
| SPM-207 | JWT validation configuration | `ConfigureNhJwtBearerValidationOptions` | Issuer, audience, signing key, lifetime, and zero clock skew are configured explicitly. |
| SPM-208 | CAP consumer group | `NhMessageProcessingAttribute` | One stable application group distributes messages across application instances. |
| SPM-209 | Startup extension point | `IStartupConfiguration` | Application-wide infrastructure is registered after platform defaults and outside controllers. |

## 16. Authorization implementation patterns

| ID | Case | Library surface | Verifiable outcome |
|---|---|---|---|
| SPM-210 | Multiple application roles | Identity roles and `NhPlatformClaimTypes.Permission` | Managers and viewers see different actions and receive demonstrably different 200 and 403 responses. |
| SPM-211 | Division-role permissions | division user, role, and claims with the active division | The division editor has project rights inside Sample North but not application-wide. |
| SPM-212 | ProjectPermission | consumer-owned claim type, requirement, handler, and frontend pipes | Alpha is allowed through a resource claim while Beta is not; application and division rights continue to work as higher levels. |
| SPM-213 | Authentication service override | `WithAuthenticationService<T>` + `GetClaimsAsync` | The standard endpoints, cookies, and refresh flow remain intact while large runtime claims stay out of the JWT. |
| SPM-214 | Hydrate claims per request | `IClaimsTransformation` + requestcache | Current claims are loaded once per request and deduplicated; a token for a removed user ends in 401. |

## 17. Consumer repository foundation

| ID | Case | Library surface | Verifiable outcome |
|---|---|---|---|
| SPM-216 | Shared .NET build and package policy | `Directory.Build.props` and `Directory.Packages.props` | Every backend project inherits one build baseline and resolves versionless package references from one central catalog. |
| SPM-217 | Scope-driven consumer bootstrap | plain-language scope gate, versioned profile bootstrap and post-bootstrap inspector | An empty repository receives only the confirmed service, API or management capabilities, retains extension seams for deferred work, restores the profile-relevant NewHeap packages before feature work, supports explicit external identity ownership for federated BFF portals, and fails validation when structure or integration patterns drift. |

## 18. AI tools and generated catalogs

| ID | Case | Library surface | Verifiable outcome |
|---|---|---|---|
| SPM-219 | Generated local read-only AI tool | `NhAiToolSet`, `NhAiTool`, generated `INhAiToolCatalog`, and guarded `AIFunction` invocation | A compile-time generated local function searches only the active division supplied by the authorized invocation context; typed schema binding, bounded input and mandatory budget reservation run before application work, while a denied, oversized or incomplete invocation never reaches the read service. |
| SPM-220 | Named AI model profile | `AddNewHeapPlatformAI`, `AddChatProfile`, keyed `IChatClient`, startup validation, and `INhAiModelProfileResolver` | A consumer-owned deterministic chat client resolves through a stable project-assistant profile only when its declared capabilities, classification, execution region, budget, keyed client and fail-closed budget manager satisfy startup validation. |
| SPM-221 | Authorized AI tool discovery | `INhAiToolDiscoveryService`, default-deny `INhAiToolDiscoveryPolicy`, generated catalog exposure, and scoped capability grants | The generated project search tool is visible only when an explicit discovery policy receives both an authorized division scope and the narrow projects-read capability; otherwise it is absent from model-visible discovery. |
| SPM-222 | Generated tool over MCP | `INhAiMcpToolAdapter`, official `ModelContextProtocol.Core` server/client primitives, generated `AIFunction`, and the shared discovery/invocation pipeline | The same generated project search implementation is discovered and invoked through the official in-memory MCP transport while actor-specific discovery, invocation authorization, budget, cancellation, input bounds and structured `TaskResult` semantics remain in the shared NewHeap pipeline; an ungoverned catalog cannot be exported. |
| SPM-223 | Authorized ASP.NET AI context | `AddNewHeapPlatformAIAspNet`, `IAuthorizationService`, active-division context contribution, execution scopes, and narrow capability grants | The API composes the production ASP.NET tool gate and contributes the authenticated actor's active division and projects-read grant only after existing server-side policies succeed; a browser header, anonymous principal or mismatched actor alone contributes no authority. |
| SPM-224 | Approved and verified AI mutation | `NhAiProposal`, `NhAiApproval`, `INhAiCapabilityResolver`, `INhAiBudgetManager`, `INhAiIdempotencyManager`, `INhAiToolVerifier`, and the shared invocation pipeline | An agent can change one project status only inside its authorized division and narrow manage capability after exact proposal approval and mandatory budget reservation; the same idempotency key cannot repeat or alter the side effect, and success is reported only after an independent status read verifies the result. |
| SPM-225 | Policy-bound Agent Framework adapter | `NhAiAgentDescriptor`, `INhAiAgentFrameworkAdapter`, Microsoft Agent Framework `ChatClientAgent`, named model profiles, and generated Agent-exposed tools | A stable non-human project agent returns structured creation outcomes, resolves its consumer-owned model profile and receives only attested actor-specific tools inside its allow-list and autonomy ceiling; every model call crosses shared classification, budget, deadline, usage and telemetry governance. |
| SPM-226 | Authorized hierarchical AI context | `INhAiContextResolver`, authorization-first context sources, execution-scope filtering, provenance, trust classification, conflict resolution, and content budgets | Project descriptions are retrieved only after source authorization, remain limited to the active division, carry provenance and untrusted-data metadata, and are deterministically deduplicated and budgeted before prompt formatting; instruction-like document text gains no authority. |
| SPM-227 | Versioned AI instruction asset | `NhAiTextAssetFactory`, `NhAiAssetManifest`, prompt version/hash binding, required model/tool contracts, context policy, retention, and evaluation baseline metadata | Project-agent instructions remain normal application-owned text while a content-free manifest records their stable identity, hash, provenance, capabilities, tool contracts, context policy, classification, retention, and evaluation baseline; agent and approval evidence bind the version and hash without logging content. |
| SPM-228 | Governed external MCP tool import | `INhAiMcpClientToolImporter`, explicit per-server allow-list and namespace, local effect and authorization metadata, bounded remote schemas and results, and shared invocation governance | Only the explicitly allowlisted external lookup is imported under a collision-safe namespace; its remote description grants no authority, an unlisted destructive-looking tool remains absent, and bounded arguments still cross NewHeap authorization, capability, budget, timeout, concurrency, idempotency, result-bound, and audit controls. |
| SPM-230 | Approval-gated durable AI portfolio report | `INhAiBackgroundOperationRunAdapter`, non-human run binding, application snapshot, server-created `NhAiProposal`, canonical `INhAiApprovalValidator`, exact approval signal, idempotent artifact publication, and plan/execute/verify progress | A division-scoped report binds its background-operation ID, attempt, idempotency key and fencing token to a non-human AI invocation, persists application-owned snapshot state plus a server-created canonical proposal and content-free workflow checkpoint, validates the durable approver against that proposal after wake-up, and resumes without conversation history or duplicate publication. |
| SPM-231 | Authorized provider-neutral AI ingestion | `INhAiIngestionPipeline`, authorization-before-read, immutable version leases, budgeted named embedding profiles, deterministic chunks, canonical hashes, and the standard Microsoft `VectorStore` abstraction | A division-scoped document is never opened before read and replacement-delete authorization, remains untrusted data with provenance and classification, and is embedded only after an immutable source/document/version lease plus mandatory model-budget reservation succeed; denial or conflict performs no embedding or vector write. |
| SPM-232 | Content-free AI model usage and streaming telemetry | `INhAiChatExecutor`, named profile and run-budget enforcement, OpenTelemetry activities and metrics, `INhAiUsageSink`, token/latency/TTFT accounting, and cancellation-safe streaming | Model calls and streams preserve standard Microsoft AI responses while recording bounded profile, version, token, size, latency, finish and scope metadata; prompts, responses, model deployment identifiers and provider errors are absent from usage records, cancelled streams propagate cancellation, and dependency failures end in a safe `TaskResult` completion. |
| SPM-233 | Versioned AI safety evaluation gate | Microsoft `IEvaluator`, `NhAiEvaluationDataset`, deterministic scope and injection fixtures, fail-closed metric interpretation, content-free reports, and baseline version/hash evidence | The project agent's prompt-injection and cross-division fixtures run through Microsoft AI Evaluation contracts against a versioned baseline; missing or inconclusive interpretations fail closed, while persisted reports contain hashes and metric outcomes but no prompt, response, reason or diagnostic content. |
| SPM-234 | Native AOT generated AI tool catalog smoke | `NewHeap.Platform.AI.AotSmoke`, generated `INhAiToolCatalog`, canonical schema manifest, trimming, and Native AOT publication | A trimmed Native AOT executable resolves the generated catalog and creates its local Microsoft AI function without runtime reflection discovery, proving descriptors and schema manifests remain rooted. |
| SPM-235 | Durable AI ingestion lifecycle | `INhAiBackgroundOperationIngestionAdapter`, content-free checkpoints, deterministic replacement lineage, authorized deletion, and partial-success batch results | A durable ingestion attempt resumes without reading or embedding the same document twice, replacement deletion is independently authorized before source access, immutable version/hash/key conflicts cannot overwrite vectors, and a failed batch item does not erase successful document evidence. |
| SPM-236 | Version-bound Agent Framework workflow checkpoint | Microsoft Agent Framework `CheckpointInfo`, `INhAiAgentFrameworkWorkflowCheckpointAdapter`, session identity, workflow version, state hash, and durable NewHeap checkpoint references | An official Agent Framework workflow checkpoint retains its opaque session and checkpoint identity in a content-free durable reference; a changed workflow version, checkpoint lineage or state hash cannot be resumed as the same run. |

## Coverage matrix

| Library area | Cases | Count |
|---|---:|---:|
| CRUD, controllers, services, and models | SPM-001–015 | 15 |
| Collections, filters, expressions, and projections | SPM-016–035 | 20 |
| Full, partial, and bulk mutations and validation | SPM-036–050 | 15 |
| DAL, repositories, SQL, EF, and transactions | SPM-051–060, SPM-218 | 11 |
| Authentication, identity, claims, and policies | SPM-061–075 | 15 |
| Events, Hangfire, email, and notifications | SPM-076–090, SPM-229 | 16 |
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
| AI tools and generated catalogs | SPM-219–228, SPM-230–236 | 17 |
| **Total** | **236 cases** | **236** |

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
12. SPM-219 proves the first provider-neutral AI seam: generated local functions, stable descriptors, fail-closed authorization, explicit division scope, and content-safe telemetry.
13. SPM-220 keeps model-provider selection consumer-owned while proving stable named profiles, keyed Microsoft clients, declared capability and data policy, deterministic fakes, and fail-fast startup validation.
14. SPM-221 proves tool discovery is separately authorized and default-deny: a generated tool without both authorized division scope and its narrow capability grant is absent rather than merely failing after model selection.
15. SPM-222 invokes that same generated function through the official MCP in-memory server/client transport; the adapter adds protocol metadata but cannot bypass NewHeap discovery or invocation authorization.
16. SPM-223 derives AI division scope and its narrow read grant through the existing ASP.NET authorization policy; request headers and model input remain data rather than authorization evidence.
17. SPM-224 protects a generated status mutation with exact proposal approval, scoped manage capability, idempotency and fencing, bounded execution, and an independent status re-read before success.
18. SPM-225 creates a stable non-human Microsoft Agent Framework agent from a named model profile and only the generated tools that survive Agent discovery, allow-listing, and its autonomy ceiling.
19. SPM-226 authorizes project context before retrieval, filters it to the active division, preserves provenance and untrusted-data classification, and applies deterministic conflict and content budgets.
20. SPM-227 keeps project-agent instructions application-owned while a content-free version/hash manifest binds required capabilities, tools, context policy, retention, evaluation, agent creation, and approvals.
21. SPM-228 imports only an explicitly allowlisted external MCP tool under a local namespace, discards remote authority claims, and executes the wrapper through the same NewHeap authorization and runtime guards.
22. SPM-229 supplies the missing general suspension boundary: a background-operation attempt is atomically released until one typed, owner-bound signal or its durable expiry wakes a new fenced attempt.
23. SPM-230 maps that durable boundary to a non-human AI report run with application-owned snapshot state, a versioned content-free checkpoint reference, exact approval, and idempotent artifact publication.
24. SPM-231 uses the standard VectorData abstractions behind an authorization-first, scope-filterable ingestion path with deterministic chunks, provenance, classification, embedding policy and idempotency metadata.
25. SPM-232 wraps direct and streaming Microsoft AI calls with profile/run budgets, deadlines, content-free usage and telemetry, and cancellation cleanup without replacing the standard response types.
26. SPM-233 makes model-facing changes evaluable through versioned Microsoft AI Evaluation datasets, deterministic injection and scope fixtures, fail-closed interpretations and content-free baseline reports.
27. SPM-234 publishes a generated local tool catalog as a trimmed Native AOT executable so descriptors, schema manifests and functions remain rooted without runtime discovery.
28. SPM-235 completes ingestion lifecycle behavior with durable content-free replay, exact replacement lineage, authorized deletion and per-document partial batch outcomes.
29. SPM-236 binds official Agent Framework workflow checkpoint identity to a versioned NewHeap durable reference and rejects changed workflow or state lineage.

## Follow-up for library gaps

The sample backlog is empty. Further coverage first requires OneOf schema work for
SPM-102 and an Angular server host for SPM-113 and SPM-160. These cases deliberately
remain gaps until their underlying behavior can be demonstrated end to end.
