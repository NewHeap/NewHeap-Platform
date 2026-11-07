# nh-common Provider Scope and Bundle Size Plan

Question: should `@newheap/platform-common` move from `NhCommonModule` to standalone components,
pipes and directives so that consumers ship smaller bundles? This plan records what was measured on
2026-11-07, the conclusion, and one choice per step. It is a plan, not usage documentation: the
guidance rules, the release notes and the sample cases are authoritative once it is built.

## Status

Investigated on 2026-11-07. On the same day Lars asked to fix the provider bug (F4) without a
standalone migration, because consumers might have to do a lot of work for little gain. The plan
below is revised accordingly. He took every advised option (SA1–SA5: "fix alles idd").

Built on 2026-11-07, not committed:

- **Step 1.** Done as described.
- **Step 2.1.** Done: msgpack now lands in a lazy chunk, and the management initial bundle went from
  1,662.2 to 1,648.4 KiB.
- **Step 2.3.** Done: `npm run report:bundle` in the sample front end.

The sample budgets stay as they are. The initial bundle still exceeds the 1.7 MB warning because of
ai-chat.

**Step 2.2.** Lars chose AC1 (a). It was built on 2026-11-07, not committed:

- `marked` and `dompurify` load on the first Markdown render, through `NhAssistantMarkdownLibraries`,
  which registers the load as a pending task.
- The management initial bundle is now 1,573.9 KiB.
- (b), the panel-content entry point, remains an option for later.

Background to that choice: deferring ai-chat in the sample shell cannot help. The root providers call
`provideNhAssistant`, which registers twelve services from ai-chat's main entry, so that entry is
always part of the initial graph. The shell's panel also uses most of the chat components. Real
savings need a change in ai-chat itself; that choice is AC1 below.

| # | Choice | Options |
|---|---|---|
| AC1 | ai-chat initial-bundle cost (634 KiB, plus `marked` 46 KiB and `dompurify` 30 KiB) | (a) load `marked` and `dompurify` on the first markdown render, like msgpack. This needs no consumer work, but the first message renders asynchronously. (b) Move the panel content (thread, composer, cards, markdown) to a secondary entry point that the panel loads on first open. This saves the most, but consumers must change the import paths of chat components they use directly. (c) Leave it. |

## Conclusion

- **Fix the bug directly; no standalone migration needed.** Plain `NhCommonModule` stops carrying
  root providers. They move to `forRoot(...)`, and the services that only the module provided become
  `providedIn: 'root'`. Consumers who register `forRoot(...)` once at the root, as documented, change
  nothing. Their features and standalone components keep importing `NhCommonModule`.
- **Standalone conversion waits.** It barely shrinks bundles (F2). It would not force consumers to
  change anything either, because `NhCommonModule` would keep exporting the same pieces. Its only
  real gain is that nh-common pieces can be `@defer`red. Revisit it when someone needs that.
- **Size wins that need no consumer work:**
  - load msgpack only when it is needed;
  - keep ai-chat out of the shell's initial bundle (F1), which is the largest single cost.

## Findings

### F1. Management sample production build today

`ng build management --configuration production --stats-json` with the synced packages. Initial
JavaScript: 1,662 KiB minified. The 1.7 MB budget warning fires at 1.80 MB raw, including CSS.

| Package (initial chunks) | KiB |
|---|---|
| `@newheap/platform-ai-chat` (main entry) | 633.7 |
| `@newheap/platform-common` (main entry) | 235.4 |
| `@newheap/platform-common/select` (reached through ai-chat) | 43.6 |
| `luxon` | 69.7 |
| `ngx-bootstrap-multiselect` (only `nh-form-dropdown` needs it) | 23.1 |
| `@msgpack/msgpack` (static import in `NhApiService`) | 17.4 |

These already behave well: `/two-factor` (78 KiB) and `/sanitize` (4 KiB) are separate entry points
and land in lazy chunks, and SignalR (55 KiB) is loaded with a dynamic `import()` and is lazy too.

### F2. Standalone barely changes bundle size

- The compiler emits only the used declarables per component. Page chunks have `dependencies`
  arrays of 4 to 21 entries, not the 35 that `NhCommonModule` exports.
- esbuild tree-shakes per statement but assigns each input file to exactly one chunk. A library
  entry point is one FESM file, so everything from it that *any* route uses lands in the chunk the
  root needs: the initial bundle. A minimal esbuild repro confirmed this.
- A secondary entry point only helps when the main entry does not re-export it. A second repro
  showed this with `sideEffects: true` and with `sideEffects: false`. Splitting nh-common therefore
  means new import paths, which is breaking.
- That is why ai-chat costs 634 KiB up front, although it is fully standalone: the shell uses
  `NhAssistantSubjectBadgeComponent` (`app.component.ts`, `management-layout.component.ts`) from the
  main entry, and everything the lazy assistant pages use from that same entry rides along.

### F3. Plain `NhCommonModule` carries root providers

`NhCommonModule`'s `@NgModule.providers` contain:

- `provideHttpClient(withInterceptorsFromDi(), withFetch())` and the six `HTTP_INTERCEPTORS`;
- the `ErrorHandler` mapping, the default `NH_ERROR_HANDLERS` and the app initializer;
- `NhCookieService`, `NhInternetConnectionService`, `NhTaskResultFormValidationService`,
  `NhServerSideFormValidationService` and `NhCanCancelNavigationGuard`. These are not
  `providedIn: 'root'`, so they exist only where the module is imported.

`forRoot` adds only the config and the auth mapping.

### F4. The bug

A temporary probe spec (since deleted) created a standalone component that imports plain
`NhCommonModule` under a root that registered `forRoot(...)` plus one extra application interceptor.
The probe showed:

- The component gets its own `EnvironmentInjector`, its own `HttpClient`, its own `NhCookieService`
  and its own `NhInternetConnectionService`.
- Its `HttpClient` runs only the six NewHeap interceptors. The application's interceptor is missing.
  In the sample, that is `SampleApiConnectionInterceptor`. GET deduplication runs in a separate
  instance with its own in-flight map.
- A component without the import shares the root `HttpClient`.

This applies to routed, bootstrapped and dynamically created standalone components, including modal
content. It applies equally to lazy NgModules that import plain `NhCommonModule`. Services
`providedIn: 'root'`, such as `NhApiService`, are not affected. The sample imports plain
`NhCommonModule` in seven standalone components; `PlatformPlaygroundComponent` also injects
`HttpClient` directly. Our guidance recommends the import, and no spec in this repository relies on
the module-local providers.

## Plan

### Step 1: fix the provider scope (nh-common)

1. Move the providers from `@NgModule.providers` into `forRoot(...)`, ahead of the config and auth
   mapping and in the same order. Keep `declarations`, `imports` and `exports` unchanged, so every
   template keeps working.
2. Make `NhCookieService`, `NhInternetConnectionService`, `NhTaskResultFormValidationService`,
   `NhServerSideFormValidationService` and `NhCanCancelNavigationGuard` `providedIn: 'root'`.
   There is then one instance per application, and they no longer depend on where the module is
   imported.
3. Tests:
   - turn the probe into a regression spec: a standalone component and a lazy NgModule that import
     `NhCommonModule` share the root `HttpClient`, its application interceptors and the services
     above;
   - pin the root `HTTP_INTERCEPTORS` order for `importProvidersFrom(NhCommonModule.forRoot(...))`
     and for `imports: [NhCommonModule.forRoot(...)]`, with an application interceptor registered
     before and after;
   - replace the spec "preserves legacy module providers while forRoot adds only config and auth
     mappings".
4. Sample:
   - no import changes;
   - show that `PlatformPlaygroundComponent`'s direct `HttpClient` request now passes
     `SampleApiConnectionInterceptor`;
   - extend the existing root-configuration case of `nh-frontend-root-configuration`.
5. Guidance:
   - keep "`forRoot` once at the root, plain `NhCommonModule` in features" and add a verification
     line that a feature shares the root `HttpClient`;
   - update the `AGENTS.md` rule at lines 321–324 ("Do not move its legacy module providers…"),
     because this change moves them on purpose, then regenerate.
6. Release notes in `v-next.md`, under `## @newheap/platform-common: provider scope`:

   | Breaking change | Required action |
   |---|---|
   | Plain `NhCommonModule` no longer registers `HttpClient`, the interceptors, `ErrorHandler` or the app initializer | Register `NhCommonModule.forRoot(...)` once at the root. The documented setup already does this, so nothing changes there. |
   | A lazy NgModule or a test that got its own `HttpClient` from plain `NhCommonModule` now uses the root one | Move feature interceptors to the root, or add `provideHttpClient(...)` to that module. In tests, add `provideHttpClient()` and `provideHttpClientTesting()`. |

7. Verify:
   - `npm run test:affected`;
   - `nh-common:test:compatibility` (Angular 20.3, 21 and 22);
   - both sample builds, including their prerender output;
   - the API snapshot, which is unchanged apart from the `providedIn` metadata.

### Step 2: size wins without consumer work

1. Load msgpack on demand in `NhApiService`, as SignalR already is: a dynamic `import()` only for
   msgpack collection responses, taking 17 KiB out of everyone's initial bundle.
2. ai-chat in the shell:
   - measure how much leaves the initial bundle when the sample shell `@defer`s its ai-chat pieces;
   - document that pattern for consumers;
   - add a small shell entry point to ai-chat only if deferring is not enough.
3. Add a bundle report: a small script over the `stats.json` metafile that prints initial KiB per
   package, as was done by hand for F1. Tighten the sample budgets once the wins land.

### Not now

- Converting the 34 module-declared declarables to standalone. It needs no consumer work, because
  `NhCommonModule` keeps exporting them, but it gains little until someone wants to `@defer` an
  nh-common piece.
- Splitting nh-common into more entry points. It changes import paths (F2), so it only pays off with
  a measured win.

## Choices

Each step offers options. **(a)** is advised.

| # | Choice | Options |
|---|---|---|
| SA1 | How to fix F4 | **(a) move the providers to `forRoot` (Step 1); no work for the documented setup;** (b) add a separate provider-free module and keep the old one as it is. Consumers must then change every feature import, and the bug remains for those who do not. |
| SA2 | Version for Step 1 | **(a) release-notes rows as breaking (nh-common major), because provider scope is compatibility-sensitive;** (b) ship it as a bug fix in a minor |
| SA3 | Module-only services | **(a) `providedIn: 'root'`;** (b) keep them only in `forRoot`'s providers |
| SA4 | Step 2 | **(a) do msgpack and the ai-chat measurement right after Step 1;** (b) later |
| SA5 | Standalone conversion | **(a) not now;** (b) do it now. It still needs no consumer work but is library work for little gain. |

## Risks

- Interceptor order, the app initializer and the `ErrorHandler` mapping are compatibility-sensitive
  (`AGENTS.md`). Step 1 keeps their order at the root and pins it with tests.
- Server rendering: `NhServerHttpInterceptor` and `NhCookieService` (`REQUEST_CONTEXT`) behave
  differently on the server. Check the sample builds' prerender output.
- A consumer that registers `NhCommonModuleConfig` and `NhAuthService` by hand, without `forRoot`,
  would lose the interceptors. This is undocumented; the first release-notes row covers it.
