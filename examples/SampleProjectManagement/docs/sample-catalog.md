# Sample catalog

This solution uses one small project-management domain to demonstrate NewHeap
features from the frontend through to the database. A sample is complete only
when it:

1. can be executed from one of the frontends;
2. uses the real NewHeap type instead of a local imitation;
3. explains in the UI why and when to use the pattern;
4. has a small, focused test where that adds value.

## Available workbenches

| Workbench | Examples | Main NewHeap mechanisms |
|---|---|---|
| Project register | CRUD, composite, short projection, partial and bulk operations, and outbox events | thin base controller, concrete service, transaction scope, `NhProjection`, `BulkAsync` |
| Collection playground | LIKE, filters, OR and AND, paging, page size, and search debounce | collection request and result models, and collection components |
| Authentication playground | password, OAuth, refresh, multiple roles, scoped permissions, and authentication overrides | authentication service, request claims, policies, guards, and pipes |
| Operations API | CAP, Hangfire, email, and notification delivery | event configurator, `NhHangfireUtil`, `NhMailService`, notification builder |
| Notification playground | polling, badge, mark-as-read, and archive | abstract notification component, service, and controller |
| Platform playground | text, binary, download, modal, multi-select, custom control, head, and JSON-LD | HTTP options, modal, form, head, and configuration services |
| Utility playground | strings, arrays, encoding, mutex, cookies, safe and date pipes | concrete utilities and components |
| EF Core chunks | ordered project query in bounded asynchronous batches | `IQueryable.ChunkAsync` with cancellation |
| Executable tests | collection expressions, projection, validation, SQL, and translation parity | library-only contracts without a UI abstraction |

## Library patterns

The samples are built directly on the public NewHeap surface. Collections use
the fluent request builder and edit components are opened through
`NhModalService`. The controller only translates HTTP while the concrete service
owns business rules and the outer transaction scope. Database writes are saved,
events are published through CAP inside that scope, and one commit follows only
afterward. There is no local base service or repository wrapper.

## Reusable test helpers

The consumer test project references `NewHeap.Platform.Common.Test` and
`NewHeap.Platform.AspNet.Common.Test` for DI contexts, DbContext and repository
registration, `TaskResult` assertions, and NSubstitute predicates. These are
packable support libraries for consumers. NewHeap's own regression tests are
deliberately not in those packages; they live in non-packable plural `.Tests`
projects under `src/Back-end/Tests` in the Platform repository.

The in-memory DbContext helper is intended only for isolated unit tests. Query
translation, migrations, relational constraints, and transactions still require
evidence on real SQL Server and PostgreSQL providers.

## Filtering

The frontend creates `ProjectCollectionRequestOptions` and adds only active
filters. The preferred approach uses fluent shortcuts such as `.equals()`,
`.isIn()`, `.like()`, `.orderAsc()`, and `.orderDesc()`. Composite trees use
`.and()` and `.or()` with `FilterRequestOptions`. Direct mutation of `filter`
and `orderBy` remains visible only as a lower-level alternative for dynamic
payloads and diagnostics. `NhApiService.getCollection` serializes paging,
search, order-by, and the filter tree into query parameters. The backend accepts
a filter only when the target field is annotated for it.

Rebuild filters whenever UI state changes so stale filter values do not remain
silently in the request.

## Root configuration and HTTP interceptors

The sample registers `NhCommonModule.forRoot(...)` exactly once at the
application root. Standalone and lazy components then import only
`NhCommonModule` for shared directives, pipes, and components. This follows the
existing consumer arrangement; the library's legacy providers deliberately
remain at module level for compatibility.

`deduplicateGetRequests` remains `false` by default in the library so an upgrade
does not silently change existing application behavior. The sample explicitly
sets it to `true` because that is the recommended setup for new implementations.
Only identical concurrent GET requests share an observable; `finalize` removes
the entry so a later request reaches the network again. In addition to URL and
query, the key includes `Accept`, language, authorization, cookie, and active
division. Requests from different users or divisions are therefore never shared.

This sample does not change the existing URL matching or token logic of the
authentication, active-division, and server interceptors. The application
therefore configures explicit API bases and demonstrates only optional request
deduplication.

## Backend configuration for runtime and automation

The API and AppHost explicitly pass their `args` to the NewHeap configuration
extensions. Runtime, migration tooling, and pipelines therefore use the same
appsettings and secret substitution. Environment variables override files, and
CLI arguments have the highest precedence. This order also applies during the
initial step that resolves `NewHeap:PlatformCommon:AppSecretsDirectoryPath`.

A production appsetting can therefore contain a Linux path while a Windows
runner points `NewHeap__PlatformCommon__AppSecretsDirectoryPath` at its own
temporary secrets directory. In automation, prefer passing sensitive values such
as connection strings directly as environment variables; CLI is more suitable
for non-secret host settings.

## Read-only database diagnostics

SPM-218 provides a checked-in `.newheap/database-read.json` profile, a typed
JSON request, and an executable validation test for `newheap-db`. The request
keeps the project ID in the parameter collection instead of concatenating it
into PostgreSQL text and applies `LIMIT` in addition to the request-level row
and timeout limits. Standard input carries the request and standard output
contains one schema-versioned JSON result, which makes the contract suitable
for developers, Codex, and other agent environments.

The profile selects the existing NewHeap application configuration and the
`NewHeapDiagnosticsReadOnly` connection-string name. The actual connection
string remains in the normal secrets file and must use a separate login with
only the approved `SELECT` permissions. The parser, transaction rollback,
timeouts, and row/output limits are additional safeguards; the database login
is the security boundary. Platform integration tests prove that the same login
can read but cannot update on real SQL Server and PostgreSQL instances while
the diagnostic queries use provider-native `TOP` and `LIMIT` caps.

## Recommended deferred lazy dropdown

`deferLazyLoadUntilOpened` is also disabled as a library default for backward
compatibility. SampleProjectManagement explicitly enables it in the shared root
configuration. The utility playground demonstrates the recommended flow live:
an already selected value is fetched immediately through
`selectedLazyLoadLambda`, while the full collection loads only when the control
is first opened. Request counters make the difference visible.

Identical concurrent lookups for the same selection share one active request,
and the completed selection is reused while the value stays the same. When the
value changes, an old active lookup is terminated. An existing screen can opt
out locally with `deferLazyLoadUntilOpened: false`, but new samples present the
opt-in as the preferred approach.

## Public, composite, and short surfaces

`GET /public/projects` derives directly from `PublicNhBaseController`, is exposed
with `AllowAnonymous`, and obtains its query policy from
`ProjectCollectionSampleService`. The controller therefore contains only HTTP
and collection mapping; the choice to return only active and completed projects
remains in the service.

`/project-composites` uses the concrete NewHeap composite controller and service
bases end to end. `ProjectCompositeService` delegates domain mutations to
`ProjectService`, so the composite surface does not acquire a second set of
business rules. The NewHeap mapping profile builds the project with its tasks.
The sample registers its profile through the source-compatible
`ConfigureAutoMapper` entry point. `NewHeap.Platform.Mapping` applies its
recursion-depth guard to both platform and consumer mappings, maps nested task
collections, and keeps entity navigation references intact when a mutate model
does not expose those members. `ProjectMappingFeatureProfile` also uses
`IncludeBase` to inherit a configured base view into a derived detail view and
maps status metadata into an `IReadOnlyDictionary`, providing executable
evidence for base-map and dictionary compatibility.

`GET /projects/short` executes the real short-projection extension with an
explicit `Project -> ProjectShortViewModel` expression. The source query defines
a stable name order and the response contains only ID, key, and name.

## Expression resolver

The collection playground calls
`GET /projects/expression-resolver?taskTitle=...`. The service maps the public
key `open-task-title` through `WithFilterable` to
`project.Tasks.Select(task => task.Title)`. The resolver turns this into
`Tasks{any}.Title`, which is valid request notation for an any filter on task
titles.

The remaining boundary is not the request syntax. In the current generic
processor, a navigation declared as `ICollection<ProjectTask>` is not recognized
as a generic collection during the final expression construction step. It then
tries to read `Title` from `ICollection` itself. The sample isolates this
behavior and shows the generated key; a concrete collection shape can execute
the same request.

## Partial update

`ProjectController.UpdatePartial` exposes a protected, authorized `PATCH` route
that accepts a top-level JSON object and delegates directly to
`DoUpdatePartial`. The controller allow-lists status, deadline, and description;
the base controller maps only supplied properties to typed setters and rejects
unknown, invalid, or forbidden properties before calling the service.

Both Angular applications exercise that endpoint through
`ProjectApiService.updateStatus`, which delegates to
`NhBaseApiService.updatePartial<void>(id, { status })`. The shared library sends
an HTTP PATCH to the standard entity route. Because the endpoint returns 204 No
Content, the applications apply the known status locally only after success and
restore the previous selection after an error.

The service loads the existing entity, builds the regular mutate model, applies
only the mapped setters, and runs `PreparePartialUpdateMutateModelAsync` before
the normal update validation. The project service uses that hook to trim and
normalize the patched description consistently with a full update.
`ProjectService.UpdatePartialAsync` keeps the transaction and project-update
event in the application service. The JSON mapper also respects configured JSON
names and property-level Newtonsoft converters.

For domain-specific commands, an explicit setter remains a useful lower-level
alternative. `ProjectController.UpdateStatus` calls the status service method,
which ultimately selects the field with:

```csharp
calls => calls.SetProperty(x => x.Status, mutateModel.Status)
```

## Bulk update

The HTTP mutate model contains only serializable data: IDs, status, and
`ContinueOnError`. The controller translates this into the delegate structure
expected by `BulkCRUDMutateModel.UpdatePartial`. `BulkAsync` manages save
changes, the transaction, and rollback. The response reports success and errors
per item.

Do not use `Promise.all(ids.map(update))` when the operation must be atomic; those
are separate HTTP transactions. The status bulk response therefore preserves
every `BulkCRUDResultModel` item as an ID, success flag, and error list. The UI
splits those results into successful and failed groups; counts are only a
summary of those concrete results.

## Transaction and CAP outbox

The API uses CAP with `UseEntityFramework<SampleProjectManagementDbContext>` and
the RabbitMQ resource from Aspire. The domain write and outbox record therefore
share the same SQL transaction. The application service publishes before
`CommitAsync`; CAP may send the message to RabbitMQ only after the commit.

`POST /projects/transaction-rollback-sample` is a deliberate diagnostic case.
The service stores a project, publishes the event in the same scope, and then
calls `RollbackAsync`. The response contains the project ID and event ID. The
project must subsequently return 404 and the event ID must not appear in
`GET /library-samples/events`. The Transactions workbench performs both checks
live without putting domain logic in the controller.

When `PublishAsync` throws, `ProjectService.CreateAsync` explicitly rolls back
and never calls `CommitAsync`. Nested partial and bulk operations receive a
non-owning scope; only the outer service can commit the real transaction.

`POST /project-composites/with-initial-task` also demonstrates the explicit
`ITransaction` variant. `ProjectSetupService` opens the outer transaction, lets
`ProjectService` and `ProjectTaskService` participate in it, and commits only
after both mutations and their events succeed. A failure in either step rolls
back the complete setup.

## Edit modal

`ProjectEditModalComponent` supports both create and update operations. The
mutate model deliberately contains no `creationDateTime` or
`lastModifiedDateTime`. The register opens the modal as follows:

```ts
const modal = modalService.open(
  ProjectEditModalComponent,
  new NhModalOptions({ modalClasses: 'large' }),
  { project, lifecycleReporter }
);
```

The modal content derives from `NhModalMutateBaseComponent`. `appOnInit` chooses
create or update and initializes form data; `appAfterViewInit` reports that the
view is available, and `appOnDestroy` performs component-specific cleanup. The
Angular hooks owned by the base are not overridden. `NhModalComponent` itself is
the shell managed by `NhModalService`, not the base for consumer content.

After `created` or `updated`, the register updates the concrete row and then
closes the modal. The parent subsequently receives the `closed` event through
`modal.onClose`, disposes both content subscriptions, and displays the event
order in the UI. This also works with local data in sample mode; with a connected
API, the exact same component uses `ProjectApiService`.

## Session expiration and mutation integrity

`provideSampleProjectManagement(...)` starts one `SampleAuthSessionService` for
both Management and Workspace. The coordinator observes
`sessionExpirationInformationChanged`. When a previously valid session expires,
it clears local token state and navigates to `/auth/login` with
`reason=session-expired` and a local `returnUrl`. The login page explains the
redirect. In the authentication workbench, the Start 10-second session button
starts a reproducible end-to-end check while the NewHeap expiration stream shows
the remaining time live.

Project mutations also use explicit connection state. Only a browser network
failure with HTTP status `0` enables local demo mode. A server response with 400,
401, 403, or 500 proves that the API is reachable and must never cause a local
success mutation. A rejected status update therefore restores the previous
selected value in both apps. Create, update, delete, and bulk operations display
success only after the API confirms the action; a local simulation has its own
visual status and explicitly says that the API was not changed. Mutation
controls are visible only with the same `app.project.manage` permission enforced
by the endpoints. In addition to the evidence check, `npm run verify:samples`
runs `verify-frontend-integrity.mjs` for this behavior.

## Form validation and collection lifecycle

The interaction playground derives directly from `NhCollectionBaseComponent`.
Search, sorting, and paging pass through `beforeLoad`, `onLoad`, and `afterLoad`;
the latest events remain visible. `appOnInit` demonstrably runs before that first
load cycle, and `appAfterViewInit` only after the view becomes available. The
Angular hooks remain owned by the base so request state and the active request
subscription are preserved. The real base lifecycle sends request state to
`ProjectApiService` and falls back to the same local dataset when no API is
available.

The ModelState button retrieves a real nested error from
`GET /library-samples/validation/model-state`.
`NhServerSideFormValidationService` places `project.name` on the nested control
and the empty key on the form. The TaskResult button runs
`NhTaskResultFormValidationService` with one field error and one general error,
so both error channels remain independently visible.

## Page lifecycle and dirty navigation

`DirtyRouteSampleComponent` derives from `NhPageBaseComponent` and displays the
order of `appOnInit`, `appOnInitAndLoad`, and `appAfterViewInit` in the UI.
`appOnInitAndLoad` is the route-sensitive loading point; `appOnDestroy` is the
component-specific cleanup point. The sample deliberately does not override
`ngOnInit`, `ngAfterViewInit`, or `ngOnDestroy`, allowing the base to keep
managing metadata, breadcrumbs, route parameters, and every subscription. Its
independent project-summary request is started with `void ...catch(...)`, so page
metadata and the remaining lifecycle can continue without losing error
observation; `takeUntilDestroyed` makes component destruction safe. This is an
explicit scheduling choice: consumer code awaits a task whenever later
initialization depends on its result, and only detaches it when repeated
invocations, errors, cancellation, and stale results are safe. The same page
implements `ICancelNavigationComponent`, so dirty state activates
`NhCanCancelNavigationGuard`.

## Match-one authorization

`GET /library-samples/authorization/match-one` executes the real
`ClaimMatchOneAuthorizeAttribute` contract. A user is allowed with either the
`app.project.manage` permission or the `administrator` role; without either,
the endpoint returns `Forbid`. The attributes describe the rules while
`ProjectAuthorizationSampleService` is the concrete enforcement point.

## Application, division, and project authorization

The authentication playground contains four Development accounts with the same
local password, `Sample123!`. The accounts make the distinction among the three
scopes visible and verify it against real endpoints:

| Account | Assigned access |
|---|---|
| `sample@example.test` | manager role with application-wide view, manage, and confidential permissions |
| `viewer@example.test` | viewer role with only the application-wide view permission |
| `division-editor@example.test` | division role with project permissions in the active Sample North division |
| `project-editor@example.test` | project-specific `confidential.view` permission and `project-editor` role for Authorization Alpha |

For global access, the backend uses the existing application claim; for division
access, it uses the existing division role and claim relationships.
`ProjectPermission` is deliberately a consumer implementation: the claim type,
requirement, handler, and frontend pipes live in the sample. The claim value has
the form `{projectId}_{permission}`. The handler also verifies that the project
belongs to the active division before a project-specific claim grants access.

The precedence is explicit: an application permission applies everywhere, a
division permission applies inside the active division, and a project permission
applies only to the named project. The UI uses the same hierarchy to show or
hide actions. Probe buttons remain available so a hidden action can also be
shown to receive a `403` response from the API.

## Authentication service overrides and request claims

The preferred route for consumer-specific authentication behavior is to preserve
the existing NewHeap flow and replace only the service:

```csharp
options.WithAuthenticationService<SampleAuthenticationService>();
```

`SampleAuthenticationService` derives directly from `NhAuthenticationService`.
The sample override normalizes the username and customizes `GetClaimsAsync`:
stable application claims remain in the JWT, while large and mutable division
and project claims stay out. Password login, cookies, refresh tokens,
impersonation, and account endpoints therefore remain standard NewHeap endpoints.

For backend authorization, the omitted claims are restored before every request
through the standard ASP.NET Core hook:

```csharp
builder.Services.AddScoped<
    IClaimsTransformation,
    SampleRuntimeClaimsTransformation>();
```

The transformer reads current claims through `INhUserManager`, stores the result
in `HttpContext.Items`, and adds only missing type and value combinations.
Multiple authentication calls in the same request therefore cause neither an
extra database read nor duplicate claims. The account endpoint continues to send
the complete claim set to the frontend, so frontend pipes see the same rights.
If the user behind a still validly signed token no longer exists, the user
manager returns an empty claim set and the transformer makes the principal
anonymous. The request then ends with `401` through the normal pipeline.

`GET /authorization-samples/overrides/runtime-claims` displays the authentication
service that was actually selected and the claims rehydrated for that request.
The authentication playground executes this endpoint interactively.

## API-to-API client

`SampleProjectManagementApiService` shows how a backend service calls another
NewHeap API. Registration is placed directly on the service provider:

```csharp
builder.Services.AddNhApiClient<SampleProjectManagementApi>(
    builder.Configuration.GetSection("ApiClients:SampleProjectManagement"));

builder.Services.AddScoped<
    ISampleProjectManagementApiService,
    SampleProjectManagementApiService>();
```

The `SampleProjectManagementApi` marker lets multiple endpoint services share one
client configuration. The library registers the named `HttpClient`, factory, and
optional authentication handler itself.

The derived service only needs to describe the endpoint:

```csharp
public sealed class SampleProjectManagementApiService
    : BaseNhApiService<SampleProjectManagementApi>
{
    public SampleProjectManagementApiService(
        ILogger<SampleProjectManagementApiService> logger,
        INhApiHttpClientFactory<SampleProjectManagementApi> httpClientFactory)
        : base(logger, httpClientFactory)
    {
    }

    public Task<TaskResult<SampleApplicationInfoModel>> GetApplicationInfoAsync(
        CancellationToken cancellationToken = default)
    {
        return DoGetAsync<SampleApplicationInfoModel>("/", cancellationToken);
    }
}
```

The Aspire AppHost places the current HTTP endpoint URL in
`ApiClients__SampleProjectManagement__BaseAddress`. As a result,
`GET /samples/api-client` makes a real API call back to the anonymous root
endpoint without hard-coding a port.

For a protected target API, add `Authentication` under the same configuration
section. Configure the password through secrets or environment variables, not
in the committed JSON file.

## Audit: library boundaries

The additional audit checks public library behavior around `ChunkAsync`, outer
transaction scopes, Hangfire, and locks. Complete business flows, the CAP
outbox, publication before commit, and rollback are present as executable
SPM-189 through SPM-200 cases. SPM-201 through SPM-209 also test boundaries that
are easy to miss: batch size and cancellation, server-side semaphore release
after a failure, safe formatting, and Identity and JWT transformations.

Application-specific channels, pipelines, and integrations are not separate
NewHeap cases when they demonstrate no public library surface. Only a real
external boundary receives a separate concrete sample.

## Complete library coverage

The traced catalog is in the
[NewHeap library sample plan](library-sample-plan.md). The Angular catalog is
generated for every build from that plan and
`sample-implementation-status.json`.

The current status is **215 implemented, 0 partial, 0 planned, and 3 gap cases
out of 218**. Implemented cases show evidence paths to tests, endpoints, or
frontend code; only the remaining gaps stay explicitly visible.

- SPM-033 is now a working resolver example: `WithFilterable` accepts the selector and generates `Tasks{any}.Title`. SPM-031 and SPM-032 cover concrete collection filtering separately;
- SPM-102: the OneOf OpenAPI transformer does not yet construct variants;
- SPM-113 and SPM-160: the SampleProjectManagement frontends do not yet have their own Angular SSR target and host.

This makes completed work and focused follow-up immediately visible; nothing is
incorrectly presented as implemented.
