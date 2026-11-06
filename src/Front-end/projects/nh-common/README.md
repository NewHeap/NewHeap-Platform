# @newheap/platform-common

Shared Angular components, services and application infrastructure for NewHeap applications.

The package supports Angular 20.3.28+, 21 and 22. Install peer dependency versions compatible with the application's Angular version. Run `npm run nh-common:test:compatibility` from `src/Front-end` to build the packaged library and compile consumers for all three Angular versions.

## Installation

The package is public on npmjs.org and installs without a registry token. An optional project `.npmrc` can make the public source explicit:

```text
registry=https://registry.npmjs.org/
@newheap:registry=https://registry.npmjs.org/
```

Install the package:

```bash
npm install @newheap/platform-common @newheap/nh-toastr
```

View available versions on [npmjs.org](https://www.npmjs.com/package/@newheap/platform-common). For NuGet, npm and AI-plugin installation, see [Consume public packages](../../../../docs/how-to/consume-public-packages.md).

## Usage

Import the core module once in the root of the application, for example in `AppModule`:

```typescript
import { NhCommonModule, NhCommonModuleConfig } from '@newheap/platform-common';

@NgModule({
  imports: [
    NhCommonModule.forRoot(new NhCommonModuleConfig({
      baseUrl: environment.baseUrl,
      apiBaseUrl: environment.apiBaseUrl,
      authApiBaseUrl: environment.apiBaseUrl,
      language: environment.defaultLanguage,
      defaultLanguage: environment.defaultLanguage,
      supportedLanguages: environment.supportedLanguages,
      culture: environment.defaultCulture,
      defaultCulture: environment.defaultCulture,
      environment: environment.name,
      cookieDomain: environment.cookieDomain
    }), AppAuthService)
  ],
  bootstrap: [AppComponent]
})
export class AppModule {}
```

`AppAuthService` is the application's `BaseNhAuthService` implementation. Set `apiBaseUrl`
and `authApiBaseUrl` to where the API is, for example `/api` or `https://api.example.com`.
The interceptors add the access token and the active division only to requests on that origin
whose path is the base path or continues it after a `/`. An empty base stands for the
application's own origin, so requests to other origins never receive them.

## List building blocks

Standalone components for collection pages; import them where they are used:

```html
<nh-pager [response]="collectionResponse" [disabled]="isLoading" (pageChange)="setPage($event)" />
<nh-skeleton shape="row" [count]="5" />
<nh-empty-state variant="no-results" [heading]="'projects.no-match' | translate">
  <button type="button" (click)="clearFilters()">Clear filters</button>
</nh-empty-state>
<nh-background-operations-badge (openRequested)="openOperations()" />
```

`nh-pager` pages a `CollectionHttpResponse` or any `{ page, itemsPerPage, totalCount, resultCount }`
object. `nh-empty-state` has the `no-data`, `no-results` and `error` variants, an
`nhEmptyStateIcon` slot and projected actions. `nh-skeleton` hides its shapes from assistive
technology and announces one polite loading status. `nh-background-operations-badge` counts the
active operations of `NhBackgroundOperationStore.watchAll()` in the browser. The components bring
English and Dutch texts under `nh-common.` that the application's translations override. Provide
`NH_COMMON_ICONS` in the root, for example `{ previous: 'ph ph-caret-left', next: 'ph ph-caret-right' }`,
to use the application's icon library, and theme them with the `--nh-color-*` custom properties.

## Migrating toast notifications

The protected `toastrService` on collection and mutate base components now uses `NhToastrService` from `@newheap/nh-toastr`. Remove `ToastrModule.forRoot(...)`, register `provideNhToastr({ positionClass: 'toast-bottom-right' })` in the root providers, import `NhToastrContainerComponent` and render one `<nh-toastr-container />` in the application shell. Configure translation providers as usual.

Replace direct `ToastrService` injections with `NhToastrService` and remove `ngx-toastr` and its global stylesheet after migrating all callers. Common calls such as `success`, `error`, `info`, `warning` and `clear` remain available; custom legacy toast components and options outside `NhToastrConfig` require adaptation. See the [toast configuration and API](../nh-toastr/README.md).

## Two-factor components

`BaseNhAuthService` covers the two-factor API: challenges, required enrollment, e-mail codes,
remembered devices, passkeys and account settings. The optional standalone components live in a
separate entry point, so applications that do not import it ship none of their code:

```typescript
import {
  NhPasskeyLoginButtonComponent,
  NhTwoFactorChallengeComponent,
  NhTwoFactorEnrollmentComponent,
  NhTwoFactorSettingsComponent
} from '@newheap/platform-common/two-factor';
```

The components inject `NhAuthService` and add their English and Dutch texts under
`nh-two-factor.`; keys the application defines win. The bundles are also available as
`@newheap/platform-common/two-factor/i18n/en.json` and `nl.json`. Call `provideNhTwoFactor()`
to register the texts at startup when a custom login page translates the `nh-two-factor.`
failure keys without the components. The authenticator QR code is a PNG data URI, so a Content
Security Policy must allow `img-src data:`.

## Sanitized rich text

Show untrusted HTML, such as pasted e-mail or Word content or text that users wrote, through the
HTML sanitizer in the optional `@newheap/platform-common/sanitize` entry point. It uses DOMPurify,
an optional peer dependency that only this entry point needs:

```bash
npm install dompurify
```

```typescript
import { NhSanitizeHtmlPipe, provideNhHtmlSanitizer } from '@newheap/platform-common/sanitize';
```

```html
<div [innerHTML]="message.body | nhSanitizeHtml"></div>
```

The default allow-list keeps the structure and inline formatting of rich text: paragraphs,
headings, lists, tables, links, quotes and emphasis, without `class`, `id` or `style`. Scripts,
styles, event handlers, frames, embedded objects, forms and SVG are always removed, also when an
allow-list names them. Links keep only `http`, `https`, `mailto` and `tel` URLs, get
`rel="noopener noreferrer"` and open in a new tab. Configure the allow-list with
`provideNhHtmlSanitizer({...})` at the root or in a component, or per call; spread
`NH_HTML_SANITIZER_DEFAULT_TAGS` to extend it. `NhHtmlSanitizer.sanitize(html)` returns the
sanitized string for code. During server-side rendering there is no DOM, so the HTML is escaped
and shows as text until the browser renders it.

`safeHtml` is deprecated: it calls `bypassSecurityTrustHtml`, which skips Angular's sanitizer
instead of sanitizing, so use it only for HTML the application wrote itself.

## Relative times

`nhRelativeTime` shows a moment relative to now in the active language with
`Intl.RelativeTimeFormat`, for example "3 minutes ago" or "in 2 days":

```html
<span>{{ task.lastModifiedDateTime | nhRelativeTime }}</span>
<span>{{ task.dueDateTime | nhRelativeTime: now : { style: 'short' } }}</span>
```

The unit goes from seconds to years and the amount is rounded to the nearest whole unit. Without a `now` argument the
pipe keeps itself current in the browser through one shared timer, also in `OnPush` and zoneless
components; pass `now` for deterministic tests and server renders. `nhFormatRelativeTime(value,
now, language)` formats the same text in code.
