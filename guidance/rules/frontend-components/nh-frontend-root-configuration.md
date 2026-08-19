---
id: nh-frontend-root-configuration
title: "Root configuration with compatible opt-ins"
area: frontend
reference: frontend-components
summary: "Register NhCommonModule once at the root and explicitly enable recommended optimizations without changing library defaults for existing consumers."
sample-cases: ["SPM-112", "SPM-121", "SPM-215"]
public-symbols: ["NhCommonModule", "NhHttpNhCommonModuleConfig", "NhFormDropDownNhCommonModuleConfig"]
skills: ["newheap-consumer-development"]
providers: ["frontend"]
risk: critical
---
## Preferred approach

Call `NhCommonModule.forRoot(...)` exactly once at the application root and import only `NhCommonModule` in features. For new implementations, explicitly enable `deduplicateGetRequests` and `deferLazyLoadUntilOpened` in the root configuration. Use translated option builders for enum dropdowns. Let a deferred dropdown resolve an existing selected value through `selectedLazyLoadLambda` before it opens, and reuse the same in-flight lookup.

GET deduplication remains GET-only, ends when the source request finalizes, and distinguishes authorization, cookie, language, and active-division headers. The library defaults for these opt-ins remain `false` for upgrade compatibility.

## Avoid

- Repeating `forRoot` in a lazy feature or standalone component.
- Turning a recommended sample opt-in into a new global library default.
- Deduplicating POST, PUT, PATCH, or DELETE requests.
- Duplicating enum labels or numeric assumptions in templates.

## Verification

Test with the opt-ins enabled in the consumer and test the library defaults separately as `false`. Verify request deduplication with different authorization, division, and language headers, and test a deferred dropdown with a preselected value.
