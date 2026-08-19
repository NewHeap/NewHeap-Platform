# Repository instructions for AI coding agents

These instructions apply to the entire repository. More specific `AGENTS.md`
files may add rules for a subtree, but may not weaken the requirements below.

## Primary rule: library changes and samples stay in sync

## Repository language

- Write all documentation, coding-agent instructions, skills, guidance rules,
  sample-case metadata, generated narrative, comments intended as examples, and
  operational how-to material in English.
- Every executable sample and user-facing sample application must provide a
  complete English experience. Additional languages are welcome; when they are
  present, keep their translation key sets aligned with English.
- Keep identifiers, public API names, package names and established domain terms
  unchanged. Translate explanatory prose and user-facing example text, not code
  symbols.
- Treat non-English prose in canonical documentation or AI instructions as
  validation drift. Fix the canonical source and regenerate derived artifacts
  rather than editing generated copies.

For public or behaviorally observable NewHeap library work, use the repository
skill at `skills/newheap-library-maintenance/SKILL.md`. For implementation work
in a consuming application, use
`skills/newheap-consumer-development/SKILL.md`. These skills are versioned with
the libraries; their generated references must not drift from executable sample
evidence.

The canonical consumer skill is mirrored into the versioned
`plugins/newheap-platform` distribution by `npm run guidance:generate`. Never
edit the plugin's generated skill copy directly. Bump the guidance and plugin
version for a distributable guidance change, keep package compatibility metadata
generated, run `npm run plugin:validate`, and use
`tools/guidance/install-consumer-skill.mjs` when pinning the skill into a
consumer repository under `.agents/skills`.

`examples/SampleProjectManagement` is the executable documentation for the
NewHeap libraries. Treat it as part of every library change, not as optional
follow-up work.

A library change includes:

- a new or changed public type, method, option, extension, service registration,
  Angular export or configuration key;
- a behavioral change or bug fix in existing public functionality;
- new EF Core, query, repository, transaction, provider or raw SQL behavior;
- a changed recommended usage pattern, even when the public signature stays the
  same.

For each such change, before considering the work complete:

1. Search the canonical case registry at
   `examples/SampleProjectManagement/docs/cases/sample-case-registry.json` and
   the atomic rules under `guidance/rules` for the affected surface. Extend the
   existing case and rule instead of creating a duplicate.
2. Add or update an executable example in `examples/SampleProjectManagement`.
   A code fragment in Markdown is not an executable example.
3. Show the preferred API clearly. Lower-level or legacy alternatives may remain,
   but must be labelled as alternatives rather than the recommended approach.
4. Add a focused regression or behavior test and a reproducible verification
   path proportional to the change.
5. Update the canonical registry, its evidence paths and the applicable atomic
   guidance rule. Update `docs/sample-catalog.md` when human narrative changes.
6. Run `npm run guidance:generate` and regenerate `sample-cases.ts`. Never edit
   generated plan, status, consumer-guide, skill-reference, manifest or
   `sample-cases.ts` files by hand.
7. Refresh `guidance/public-api-snapshot.json` for an intentional public-surface
   change and run the guidance, skill-eval and sample evidence validators.
8. Do not mark a case implemented when it only
   has documentation or non-executable pseudocode.

An internal refactor with no observable behavior change does not require a new
case, but the existing case and regression tests must continue to pass. If a real
sample cannot be built because the library surface is incomplete, register an
explicit `library-gap` with the missing capability and do not fake the behavior in
the sample.

Do not refer from the sample to copied comparison projects, temporary workspaces
or external application implementations. The sample must explain and prove the
NewHeap API on its own.

Do not copy identifying names, domains, repository paths, certificates, stack
traces or fixtures from internal comparison/consumer applications into this
repository's source, samples, documentation, guidance, skills or distributable
artifacts. Replace useful patterns with neutral NewHeap-owned examples.

## Where code belongs

Keep dependencies pointing from general contracts toward specific
implementations, never the reverse.

| Kind of change | Owning area |
|---|---|
| Provider-independent algorithms, primitives and extensions | `src/Back-end/Libraries/NewHeap.Platform.Common` |
| ASP.NET, repository and provider-independent EF/query abstractions | `src/Back-end/Libraries/NewHeap.Platform.AspNet.Common` |
| Provider-independent media contracts and behavior | `NewHeap.Platform.Media.Core` or the relevant neutral media project |
| SQL Server implementation, registration, SQL and migrations | the matching `.SqlServer` project |
| PostgreSQL implementation, registration, SQL and migrations | the matching `.PostgreSql` project |
| Sample domain models and services | `examples/SampleProjectManagement/src/Back-end/Libraries/SampleProjectManagement.Core` |
| Sample entities, relationships, DbContext and sample-owned migrations | `examples/SampleProjectManagement/src/Back-end/Libraries/SampleProjectManagement.DAL` |
| Sample HTTP composition and endpoints | `examples/SampleProjectManagement/src/Back-end/Applications/SampleProjectManagement.Api` |
| Reusable Angular library behavior | `src/Front-end/projects/nh-common` |
| Executable Angular examples | the management/workspace apps under `examples/SampleProjectManagement/src/Front-end` |

Do not put consumer entities or consumer migrations into a reusable NewHeap
library. Do not put new PostgreSQL-only behavior in a SQL Server project, or vice
versa. If shared implementation currently lives in a provider-named project, do
not deepen that coupling: move genuinely shared behavior to a neutral seam when
the change requires it.

## EF Core, LINQ and database providers

SQL Server and PostgreSQL support is the default requirement for relational
library behavior. A task is not complete merely because it works with EF Core
InMemory or with the provider used by the sample AppHost.

For every EF/query change:

1. Prefer provider-translatable LINQ and provider-neutral model configuration.
2. Check the generated/translated behavior with both SQL Server and PostgreSQL.
3. Keep provider selection and `UseSqlServer`/`UseNpgsql` wiring in the owning
   implementation or composition layer.
4. Avoid provider-specific column types, annotations, default SQL, computed SQL,
   functions and quoting in neutral code.
5. Use UTC-safe query expressions. In translated queries prefer
   `DateTimeOffset.UtcNow` or a captured UTC value; do not use
   `DateTimeOffset.Now`.
6. Keep filtering, ordering and paging server-side. Do not hide a translation
   problem with premature `AsEnumerable`, `ToList` or client-side filtering.
7. Use EF metadata/repository helpers for schema, table and column names rather
   than hard-coded identifiers where possible.
8. Treat different provider semantics—case sensitivity, null ordering, date/time,
   identity generation, precision and transaction behavior—as test cases, not
   assumptions.

When only one provider can support a feature, keep the limitation explicit:
isolate it in that provider project, expose a clear capability/registration
boundary, and record the other provider as a documented `library-gap`. Never let
the other provider fail accidentally at runtime.

## Raw SQL rules

Raw SQL is provider-specific until proven otherwise.

- Prefer EF/LINQ first when it expresses the operation correctly.
- Parameterize every data value. Only explicitly allow-listed identifiers or SQL
  fragments may use the repository raw marker.
- Keep SQL Server and PostgreSQL SQL in separate, provider-labelled
  implementations. Do not build one large interpolated string with hidden dialect
  branches.
- Supply two executable provider variants for raw SQL or provider-specific query
  translation: one for SQL Server and one for PostgreSQL. These may be two catalog
  cases or two independently runnable subcases, but each provider must have its
  own evidence and focused test.
- EF Core InMemory does not count as evidence for raw SQL, migrations, relational
  constraints, transactions or provider translation.

## Migrations and schema ownership

- Never hand-edit an existing migration or model snapshot.
- Never add migrations to a provider-neutral library.
- Generate a new migration with the EF CLI only when the task changes a schema
  owned by that project and migrations are explicitly part of that implementation.
- Library-owned storage with provider projects needs the corresponding migration
  in each supported provider project.
- Consumer schema changes belong to the consumer implementation. For the sample,
  that is `SampleProjectManagement.DAL`, not a NewHeap library.
- If a reusable library change requires downstream applications to migrate, state
  that clearly in the handoff and demonstrate the implementation-side migration
  in the sample where applicable.

## Sample implementation conventions

Back-end modules normally include controllers, services, entities, view models
and mutate models where applicable.

- Add a `DbSet`, relationships and repository registration for new entities.
- Follow multiple existing controllers for CRUD shape and add suitable
  authorization attributes.
- Keep controllers thin; put normalization, validation, query composition and
  orchestration in concrete services.
- Register view/mutate mappings in `AutoMapperProfileConfiguration.cs`.
- Mark a view model `Id` with `Filterable`.
- Do not include `creationDateTime` or `lastModifiedDateTime` in mutate models.

## Test project ownership

- `NewHeap.Platform.Common.Test` and `NewHeap.Platform.AspNet.Common.Test` are
  packable support libraries for tests in consuming applications. Keep them
  limited to reusable contexts, fixtures, factories, assertions and substitute
  helpers; never place NewHeap's own `[Fact]` or `[Theory]` tests in them.
- Put NewHeap library regression tests under `src/Back-end/Tests` in plural
  `*.Tests` projects with `IsPackable=false` and `IsTestProject=true`. These
  projects may reference the implementation and its reusable `.Test` helper,
  but are never release packages or consumer dependencies.
- SampleProjectManagement test projects should consume the packable `.Test`
  helpers as a real application would. They must not reference NewHeap's
  internal `*.Tests` projects.
- EF Core InMemory helpers are for isolated unit tests only. They do not replace
  SQL Server/PostgreSQL evidence for relational behavior.

### Scalar and OpenAPI controller contract

Scalar is the executable reference for sample HTTP APIs. Whenever an ASP.NET
controller or action is added or changed:

- Every HTTP action must have a concise `EndpointSummary`, a useful
  `EndpointDescription` and explicit `ProducesResponseType` metadata for the
  success response and expected error responses.
- Prefer typed response contracts. Do not return an anonymous object when the
  response is part of the API contract; Scalar must be able to render its schema.
- Document `400`, `401`, `403`, `404` and conflict responses where the action can
  actually return them. Do not advertise status codes that the implementation
  does not produce.
- Put `Authorize`/policy or `AllowAnonymous` explicitly on every action or its
  controller. Protected actions document their authentication and authorization
  responses.
- Keep route, query, body, form and service bindings explicit. For file actions,
  also document the produced or consumed content type.
- Configure the Scalar page with a stable title, OpenAPI route pattern, layout,
  theme and preferred bearer scheme. Verify both `/openapi/v1.json` and `/scalar`.
- Keep a reflection-based regression test that fails when a sample controller
  action lacks a summary, description, response metadata or authorization intent.

For Angular samples:

- Put translations under the module object and use lowercase `dash-case` keys.
- Treat both sample applications as production-quality enterprise product interfaces, not as disposable demo pages. Reuse the shared semantic tokens, typography, spacing, shell and interaction patterns; do not introduce a second visual system for an individual sample case.
- Base those shared tokens on the current NewHeap brand guide at `https://branding.newheap.com/`: darkmode-first, core ink `#000000`, primary blue `#022962`, orange accent `#FF6700`, white `#FFFFFF`, Inter/system sans typography, 12px controls and 16px cards. Use orange for focused energy rather than as a decorative wash.
- Keep the product UI calm and information-dense. Use one brand accent, restrained radii and elevation, and reserve strong colors for semantic status. Internal case numbers belong in the searchable case catalog, not in primary page headings.
- Use the repository icon library for interface icons. Do not add hand-written SVGs, emoji or Unicode arrows as button content. Every icon-only action needs an accessible name and a tooltip where its meaning is not obvious.
- Every data-driven surface must have intentional loading, empty, error and disabled states. Never render raw proxy responses, HTML or stack traces as user-facing errors; map them to localized, actionable messages while retaining technical diagnostics outside the UI.
- Keep sample shells responsive without horizontal document overflow. Mobile navigation must remain reachable by keyboard, close on route selection and Escape, and must not leave hidden controls in the tab order.
- Support the shared light and dark color schemes, visible keyboard focus and reduced-motion preferences. Do not add runtime dependencies on external font services; prefer the shared system font stack or self-hosted assets.
- After visual changes, build both apps and inspect the login, shell, navigation and changed surfaces in a browser at desktop and narrow mobile widths. Check console errors and horizontal overflow in addition to screenshots.
- Register `NhCommonModule.forRoot(...)` once in the application root, as the
  existing consumers do. Feature modules and standalone components may import
  plain `NhCommonModule`. Do not move its legacy module providers or change
  interceptor registration semantics as incidental sample work.
- For new implementations, explicitly enable
  `NhHttpNhCommonModuleConfig.deduplicateGetRequests` and
  `NhFormDropDownNhCommonModuleConfig.deferLazyLoadUntilOpened` in the root
  config, and show those opt-ins in SampleProjectManagement. They intentionally
  remain `false` as library defaults so existing consumers do not change
  behavior during an upgrade.
- In-flight GET reuse must remain GET-only, end when the source request
  finalizes, and distinguish authorization, cookie, language and active-division
  headers.
- A deferred lazy dropdown still resolves existing values through
  `selectedLazyLoadLambda` before it is opened. Reuse the same selected-value
  lookup while it is in flight.
- Treat provider scope, interceptor URL matching, lifecycle ordering, fluent
  filter serialization, falsy filter handling and enum reverse mappings as
  compatibility-sensitive library behavior. Never change them only to improve
  a sample; an intentional change needs an explicit migration plan and version.
- Mutating modal content extends `NhModalMutateBaseComponent`; other dynamic
  modal content may extend `NhModalComponentImpl`. `NhModalComponent` is the
  service-owned modal shell and is not the consumer content base. Pass
  `modalClasses: 'large'` for a large modal.
- Components derived from `NhPageTypeBaseComponent`,
  `NhCollectionTypeBaseComponent`, `NhMutateBaseTypeComponent`,
  `NhModalComponentImpl` or one of their concrete derivatives must use the
  corresponding `appOnChanges`, `appOnInit`, `appAfterContentInit`,
  `appAfterViewInit` and `appOnDestroy` extension points. Do not override the
  Angular `ngOn...`/`ngAfter...` methods owned by those bases: they preserve
  NewHeap routing, metadata, request-state, modal and subscription behavior.
- Use page `appOnInit` for one-time component initialization,
  `appOnInitAndLoad` for work that must repeat when active route parameters
  change, and `appOnInitAndLoadWithSkipBrowserInitial` only for work that must
  skip the browser hydration pass. Put component cleanup in `appOnDestroy`.
- Treat `await` inside an `appOn...` hook as an ordering decision. Await work
  when later initialization depends on it. When independent work should start
  without delaying metadata or the remaining lifecycle, detach it explicitly
  with `void task().catch(handleError)` (or keep complete error handling inside
  that task), and make repeated route invocations, cancellation and stale
  results safe. Never mechanically replace a deliberate `.then()` or `void`
  call with `await`; that changes observable scheduling and can slow page startup.
- In collection components, `appOnInit` runs before the initial load;
  `beforeLoad`, `onLoad` and `afterLoad` describe every collection request.
  Do not call `load()` again from `appOnInit` merely to trigger the first load.
- When an intermediate NewHeap/application base has meaningful `appOn...`
  behavior, await the corresponding `super.appOn...()` from the override.
  Components that do not inherit a NewHeap lifecycle base continue to use the
  normal Angular lifecycle hooks.
- Use fluent collection helpers such as `.equals()`, `.and()`, `.or()` and fluent
  ordering as the preferred examples. Show raw filter construction only as a
  clearly labelled lower-level alternative.
- Use translated enum option builders for enum dropdowns rather than duplicating
  labels or numeric assumptions in templates.
- Authorization samples show the complete chain from seeded role and claim to
  backend policy enforcement and frontend visibility. Demonstrate application
  permissions, active-division permissions and consumer-specific resource
  permissions as separate scopes.
- Keep consumer-specific resource claim types, requirements and handlers in the
  consumer implementation. Encode the resource id in the claim value, validate
  that the resource belongs to the active division, and let application or
  division permissions grant access through an explicit hierarchy.
- Customize token creation through `WithAuthenticationService<T>` and a derived
  `NhAuthenticationService`; keep the standard NewHeap endpoints, cookie and
  refresh-token behavior unless the task explicitly requires a new protocol.
- For large or volatile division/resource claims, keep the stable application
  claims in the JWT and restore current claims through `IClaimsTransformation`.
  Cache the lookup per request, deduplicate claims and test that authorization
  sees the hydrated principal. A validly signed token for a removed user must
  become anonymous and produce `401`, never a null-reference or stale access.

## Verification checklist

Run the smallest relevant library tests plus the sample checks. For a complete
library feature, expect all applicable items below:

```text
dotnet test src/Back-end/NewHeap.Platform.sln
dotnet test examples/SampleProjectManagement/src/Back-end/SampleProjectManagement.slnx
npm run sample:structure
cd examples/SampleProjectManagement/src/Front-end
npm run generate:samples
npm run verify:samples
npm run build:management
npm run build:workspace
```

For database behavior, also run real SQL Server and PostgreSQL integration tests.
If local infrastructure prevents one provider run, do not silently replace it
with InMemory: report exactly which provider remains unverified.

The final handoff must identify the sample case/evidence added, the provider
matrix exercised and any explicit provider gap.
