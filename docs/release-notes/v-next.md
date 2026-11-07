# v-next

## @newheap/platform-common: provider scope

| Breaking change | Required action |
|---|---|
| Plain `NhCommonModule` no longer registers `HttpClient`, the interceptors, the `ErrorHandler` mapping or the configuration initializer; only `NhCommonModule.forRoot(...)` does | Register `NhCommonModule.forRoot(...)` once at the application root, as documented; features and standalone components keep importing `NhCommonModule`. |
| A lazy NgModule, standalone component or test that got its own `HttpClient` from plain `NhCommonModule` now uses the root one | Register feature interceptors at the root, or add `provideHttpClient(...)` to that module or route; in tests add `provideHttpClient()` and `provideHttpClientTesting()`. |
| A MessagePack collection response (`responseType: 'arraybuffer'`) emits after the decoder loads on first use | In tests, await the collection result instead of asserting right after `flush()`. |

## @newheap/platform-ai-chat: Markdown libraries load on first use

`marked` and `dompurify` now load on the first Markdown the assistant renders instead of with the
application, which takes about 76 KiB out of its initial bundle. Until they arrive, the text shows
escaped, as it already did during server rendering. Diagrams in `nh-assistant-document` wait for
DOMPurify before they are sanitized and drawn.

| Breaking change | Required action |
|---|---|
| Assistant Markdown renders after `marked` and `dompurify` have loaded, not during the first change detection | In tests, `await fixture.whenStable()` and call `fixture.detectChanges()` again before asserting rendered Markdown. |
