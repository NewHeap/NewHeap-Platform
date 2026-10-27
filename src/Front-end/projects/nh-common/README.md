# @newheap/platform-common

Shared Angular components, services and application infrastructure for NewHeap applications.

## Installation

The package is public on npmjs.org and installs without a registry token. An optional project `.npmrc` can make the public source explicit:

```text
registry=https://registry.npmjs.org/
@newheap:registry=https://registry.npmjs.org/
```

Install the package:

```bash
npm install @newheap/platform-common
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
