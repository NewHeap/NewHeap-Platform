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
import { NhCommonModule } from '@newheap/platform-common';

@NgModule({
  imports: [
    NhCommonModule.forRoot(new NhCommonModuleConfig({
      baseUrl: environment.baseUrl,
      language: environment.defaultLanguage,
      defaultLanguage: environment.defaultLanguage,
      supportedLanguages: environment.supportedLanguages,
      culture: environment.defaultCulture,
      defaultCulture: environment.defaultCulture,
      environment: environment.name,
      cookieDomain: environment.cookieDomain
    }))
  ],
  bootstrap: [AppComponent]
})
export class AppModule {}
```
