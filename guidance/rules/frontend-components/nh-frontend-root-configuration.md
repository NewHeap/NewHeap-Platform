---
id: nh-frontend-root-configuration
title: "Root configuration with compatible opt-ins"
area: frontend
reference: frontend-root-configuration
summary: "Register NhCommonModule once at the root and explicitly enable recommended optimizations without changing library defaults for existing consumers."
sample-cases: ["SPM-112", "SPM-121", "SPM-215", "SPM-106"]
public-symbols: ["NhCommonModule", "NhHttpNhCommonModuleConfig", "NhFormDropDownNhCommonModuleConfig", "NhCommonModuleConfig", "AuthenticationNhCommonModuleConfig"]
skills: ["newheap-frontend-development"]
providers: ["frontend"]
risk: critical
---
## Preferred approach

Call `NhCommonModule.forRoot(...)` exactly once at the application root and import only `NhCommonModule` in features. For new implementations, explicitly enable `deduplicateGetRequests` and `deferLazyLoadUntilOpened` in the root configuration. Use translated option builders for enum dropdowns. Let a deferred dropdown resolve an existing selected value through `selectedLazyLoadLambda` before it opens, and reuse the same in-flight lookup.

The package supports Angular 20.3.28+, 21 and 22. Choose peer dependency versions compatible with the consumer's Angular version, including the datatable and cookie service.

Collection and mutate bases expose `NhToastrService` through their protected `toastrService`. Register `provideNhToastr(...)` from `@newheap/nh-toastr` in the root providers and render one `NhToastrContainerComponent` in the shell. When upgrading from `ngx-toastr`, replace its module registration and direct service injections, remove its stylesheet, and adapt custom toast components or options outside `NhToastrConfig`.

GET deduplication remains GET-only, ends when the source request finalizes, and distinguishes authorization, cookie, language, and active-division headers. The library defaults for these opt-ins remain `false` for upgrade compatibility.

Set `apiBaseUrl` and `authApiBaseUrl` explicitly to where the API is, such as `/api` or `https://api.example.com`. The authentication and active-division interceptors add the access token and `X-NH-ActiveDivisionId`, and the server interceptor forwards session cookies, only to a request on the base URL's origin whose path is the base path or continues it after a `/`. An empty base stands for the application's own origin. With `authType: 'header'` the token always goes to requests under `authApiBaseUrl`, which the library's own logout, impersonation, two-factor and passkey calls need; `addAuthTokensToRequests: false` stops it only for requests under `apiBaseUrl`.

## Avoid

- Repeating `forRoot` in a lazy feature or standalone component.
- Turning a recommended sample opt-in into a new global library default.
- Deduplicating POST, PUT, PATCH, or DELETE requests.
- Duplicating enum labels or numeric assumptions in templates.
- Leaving the API bases empty when the API runs on another origin, or adding the access token to other origins in an interceptor of your own.

## Verification

Test with the opt-ins enabled in the consumer and test the library defaults separately as `false`. Verify request deduplication with different authorization, division, and language headers, and test a deferred dropdown with a preselected value. Verify that an API request carries the access token and active division, and that another origin, a longer host name that starts with the API's host and a path beside the base receive neither.

Build the package on the oldest supported Angular baseline and compile consumers on Angular 20, 21 and 22 using `npm --prefix src/Front-end run nh-common:test:compatibility`.
