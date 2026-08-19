---
id: nh-frontend-collection-query
title: "Fluent collections, filtering, and ordering"
area: frontend
reference: frontend-components
summary: "Use the fluent collection API by default and keep filtering, ordering, and paging server-side without losing falsy values or enum values."
sample-cases: ["SPM-016", "SPM-017", "SPM-018", "SPM-019", "SPM-020", "SPM-021", "SPM-022", "SPM-023", "SPM-024", "SPM-025"]
public-symbols: ["CollectionHttpRequestOptions", "FilterRequestOptions", "NhCollectionTypeBaseComponent"]
skills: ["newheap-consumer-development"]
providers: ["provider-neutral"]
risk: high
---
## Preferred approach

Build collection requests with fluent helpers such as `.equals()`, `.and()`, `.or()`, and fluent ordering. Combine free-text search, filters, sorting, and paging in one server-side request. Handle `false`, `0`, empty enum values, null, and empty text according to the chosen filter contract; do not use a truthy check that discards a valid value.

Let a collection component perform its own initial load. Use `beforeLoad`, `onLoad`, and `afterLoad` around every request and provide useful loading, empty, and error states. Show raw filter construction only as a lower-level alternative.

## Avoid

- Calling `load()` again from `appOnInit` to force the initial load.
- Client-side `ToList`, `AsEnumerable`, or array filtering to hide a provider translation problem.
- Manually assembled filter strings as the recommended example.
- `if (value)` for filters that may contain `false` or `0`.

## Verification

Test AND/OR, null, text, date ranges, multiple values, single and multi-column sorting, and paging through the frontend and API. Run backend query tests on SQL Server and PostgreSQL whenever a query shape or translation changes.
