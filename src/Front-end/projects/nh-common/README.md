# NewHeap platform - Common

## Installation

1. Create a `.npmrc` file in the root of your project with the following content:
```
registry=https://pkgs.dev.azure.com/NewHeap/NewHeap-Platform/_packaging/NewHeap-Platform/npm/registry/

always-auth=true
```

2. Make sure you have the `vsts-npm-auth` package installed globally and run authenticate with the `.npmrc` file:
```bash
# npm install -g vsts-npm-auth
# vsts-npm-auth -config .npmrc
```

3. Install the package:
```bash
npm install @newheap/platform-common
```

## Usage

Import the core module in the root of your application (for example AppModule):

```typescript
import { NhCommonModule } from '@newheap/platform-common';

@NgModule({
  ...
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
    })),
  ],
  ...
  bootstrap: [AppComponent]
})

