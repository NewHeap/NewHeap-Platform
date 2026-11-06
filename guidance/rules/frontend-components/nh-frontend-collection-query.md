---
id: nh-frontend-collection-query
title: "Fluent collections, filtering, and ordering"
area: frontend
reference: frontend-collection-query
summary: "Use the fluent collection API by default and keep filtering, ordering, and paging server-side without losing falsy values or enum values."
sample-cases: ["SPM-016", "SPM-017", "SPM-018", "SPM-019", "SPM-020", "SPM-021", "SPM-022", "SPM-023", "SPM-024", "SPM-025", "SPM-127", "SPM-157"]
public-symbols: ["CollectionHttpRequestOptions", "FilterRequestOptions", "NhCollectionTypeBaseComponent", "NhSearchInputComponent", "NhDebounceDirective"]
skills: ["newheap-frontend-development"]
providers: ["provider-neutral"]
risk: high
---
## Preferred approach

Build collection requests with fluent helpers such as `.equals()`, `.and()`, `.or()`, and fluent ordering. Combine free-text search, filters, sorting, and paging in one server-side request. Handle `false`, `0`, empty enum values, null, and empty text according to the chosen filter contract; do not use a truthy check that discards a valid value.

Let a collection component perform its own initial load. Use `beforeLoad`, `onLoad`, and `afterLoad` around every request and provide useful loading, empty, and error states. Show raw filter construction only as a lower-level alternative.

Take free-text search from `nh-search-input` or from a field with `[ngModel][nhDebounce]`. `onDebounce` reports the value once typing pauses and `preDebounce` reports each change at once. Neither reports the initial `ngModel` value, and a value still waiting when the field is destroyed is not reported, so the initial load fetches the first page and each pause in typing adds one request. Pass the visible label text to `nh-search-input` through `label`; without it the field is named by the translated `general.search` text that its placeholder shows. `inputId` puts an id on the field for a page label's `for` attribute.

## Avoid

- Calling `load()` again from `appOnInit` to force the initial load.
- Client-side `ToList`, `AsEnumerable`, or array filtering to hide a provider translation problem.
- Manually assembled filter strings as the recommended example.
- `if (value)` for filters that may contain `false` or `0`.
- Waiting for `onDebounce` to report the initial search value instead of loading the first page yourself.
- A search field that only a placeholder names.

## Verification

Test AND/OR, null, text, date ranges, multiple values, single and multi-column sorting, and paging through the frontend and API. Check that a search field reports no initial value, that rapid typing produces one request, and that the field's accessible name matches its visible label. Run backend query tests on SQL Server and PostgreSQL whenever a query shape or translation changes.
