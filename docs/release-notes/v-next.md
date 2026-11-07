# v-next

## @newheap/platform-common: provider scope

| Breaking change | Required action |
|---|---|
| Plain `NhCommonModule` no longer registers `HttpClient`, the interceptors, the `ErrorHandler` mapping or the configuration initializer; only `NhCommonModule.forRoot(...)` does | Register `NhCommonModule.forRoot(...)` once at the application root, as documented; features and standalone components keep importing `NhCommonModule`. |
| A lazy NgModule, standalone component or test that got its own `HttpClient` from plain `NhCommonModule` now uses the root one | Register feature interceptors at the root, or add `provideHttpClient(...)` to that module or route; in tests add `provideHttpClient()` and `provideHttpClientTesting()`. |
