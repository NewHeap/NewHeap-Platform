---
id: nh-auth-override-hydration
title: "Authentication overrides and claim hydration"
area: authentication
reference: authentication
summary: "Extend token creation through the intended service extension point and hydrate mutable claims per request without replacing the standard protocol."
sample-cases: ["SPM-069", "SPM-213", "SPM-214"]
public-symbols: ["NhAuthenticationService", "INhAuthenticationService", "WithAuthenticationService", "NhUserManager"]
skills: ["newheap-consumer-development"]
providers: ["provider-neutral"]
risk: critical
---
## Preferred approach

Register a derived `NhAuthenticationService` through `WithAuthenticationService<T>` when stable application claims must be adjusted during token creation. Preserve the standard NewHeap endpoints, cookie behavior, and refresh-token flow. Keep large or mutable division and resource claims out of the JWT and restore them through `IClaimsTransformation` for every request.

Cache the lookup per request, deduplicate claims, and make a removed or unknown account anonymous. A validly signed token for a user who has since been removed must result in `401`, not a null reference or stale access.

## Avoid

- Copying the login or refresh routes only to add claims.
- Storing volatile permissions in a long-lived JWT.
- Allowing an unknown user to continue as a partially authenticated principal.
- Running claim hydration more than once per request.

## Verification

Test login, refresh, expiration, an unknown user, a removed user, claim deduplication, and authorization after hydration. Verify that the frontend routes a `401` response to the login page and never displays a successful UI status for a failed mutation.
