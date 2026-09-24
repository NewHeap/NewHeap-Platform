---
id: nh-ai-aspnet-context
title: "Build authorized ASP.NET AI invocation context"
area: authentication
reference: ai-aspnet-context
summary: "Contribute authenticated actor, active-division scope, and narrow capability grants only after existing ASP.NET authorization policies succeed."
sample-cases: ["SPM-223"]
public-symbols: ["INhAiAuthenticatedInvocationContextResolver", "NhAiAspNetBuilder", "NhAiAspNetFailureCodes", "NhAiAspNetIssuerClaimMapping", "NhAiAspNetScopeAuthorizationResource", "NhAiAspNetServiceCollectionExtensions"]
skills: ["newheap-authentication"]
providers: ["provider-neutral"]
risk: high
---
## Preferred approach

Register `AddNewHeapPlatformAIAspNet` in the API host. For OIDC/JWT hosts with
unmapped claims, call the existing `UseAuthenticatedClaims` overload for one exact
issuer, or pass `NhAiAspNetIssuerClaimMapping` entries when the host accepts more
than one. Each entry owns its issuer, subject, tenant and tenant-scope claim mapping.
Use the issuer-specific `AddClaimScope` overload for a claim that belongs only to
that issuer; the existing overload projects a global claim for every resolved
issuer. Add only reviewed scalar claim scopes and explicit scope-value-to-capability
mappings. Duplicate authority claims, authority claims from multiple issuers,
missing required claims, and an issuer outside the configured set fail closed.
Map `NhAiAspNetFailureCodes.IssuerNotAccepted` to a forbidden response where a host
publishes its own HTTP execution endpoint. The resulting context keeps issuer,
subject, tenant, and a collision-resistant issuer-qualified actor ID distinct.
Use `UseAuthenticatedClaimsWithoutTenant` only for an explicitly tenantless host,
or `UseAuthenticatedClaimsForSingleTenant` to project one configured tenant without
trusting a caller-supplied tenant claim. Repeated permission/scope claims are combined
for configured capability values; issuer, subject, tenant and scalar scope claims
still reject duplicates.

For the legacy active-division flow, configure one existing
active-division authorization policy and explicit capability-to-policy mappings.
Set a bounded lowercase tool-invocation purpose with
`UseToolInvocationPurpose`. The registration supplies the production
`INhAiToolInvocationGate`: it uses the same per-request authenticated resolver,
re-authorizes descriptor policies, builds context through the shared factory,
and accepts only a bounded idempotency header.
The contributor matches the authenticated name-identifier claim to the requested
actor, calls `IAuthorizationService`, and only then copies the division ID into a
bounded execution scope and grants a lowercase dash-case capability.

Keep claim hydration, division membership, roles, resource permissions, and
removed-user handling in the existing NewHeap authentication flow. The AI
context contains opaque IDs and safe correlation metadata, not a
`ClaimsPrincipal`, token, cookie, request body, prompt, or credential. Other
hosts can provide their own `INhAiInvocationContextContributor` without taking
an ASP.NET dependency.

## Avoid

- Treating the active-division request header or a browser selection as authorization.
- Copying every user role or claim into ambient AI capabilities.
- Accepting duplicate issuer, subject, tenant, or projected scalar claims; repeated permission claims are allowed only through explicit capability mappings.
- Selecting the first matching issuer when claims for more than one configured authority are present.
- Treating an issuer-specific claim scope as global or reusing its scope key in a conflicting global registration.
- Accepting an actor, division, tenant, or capability from model/tool input.
- Storing an access token, cookie, user profile, prompt, or raw request in invocation context.
- Granting a capability when its configured authorization policy fails or is missing.
- Replacing the ASP.NET gate with an allow-all or browser-derived production gate.

## Verification

Use an authenticated principal and existing active-division policy in an API or
focused test. Verify matching actor plus successful scope/capability policies
contribute only the expected IDs and grant. Verify denied policy, missing header,
anonymous principal, and actor mismatch contribute neither scope nor capability.
Resolve the production invocation gate from the real API composition and verify
both authorized and denied tool paths.
Verify tenantless and fixed single-tenant projection separately, including repeated
permission claims and rejection of duplicate authority claims.
For a multi-issuer host, resolve one principal per mapping, verify issuer-specific
claim scopes, verify the same subject produces distinct actor IDs, and reject both
an unaccepted issuer and a principal carrying authority claims from multiple issuers.
SPM-223 is the executable reference.
