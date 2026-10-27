---
id: nh-ai-aspnet-context
title: "Build authorized ASP.NET AI invocation context"
area: authentication
reference: ai-aspnet-context
summary: "Contribute authenticated actor, active-division scope, and narrow capability grants only after existing ASP.NET authorization policies succeed."
sample-cases: ["SPM-223", "SPM-268", "SPM-302", "SPM-314", "SPM-315"]
public-symbols: ["INhAiAuthenticatedInvocationContextResolver", "NhAiAspNetBuilder", "NhAiAspNetFailureCodes", "NhAiAspNetIssuerClaimMapping", "NhAiAspNetScopeAuthorizationResource", "NhAiAspNetServiceCollectionExtensions", "NhAiSubjectAuthorizationResource", "INhAiPrincipalFactory", "INhDivisionScopedResource", "INhAiAccessSource", "NhAiAccessContext", "NhAiAccessSourceOptions", "UsePrincipalsFromAccessSource"]
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

A projected tenant keeps people out of the assistant's work for the whole application,
which belongs to no tenant. When every person of the host belongs to the one tenant
that `UseAuthenticatedClaimsForSingleTenant(issuer, tenantId)` projects, and they must
see and decide that work, also call `UseSingleTenant(tenantId)` on the assistant
builder with the same tenant (see `nh-ai-agent-runs`). Startup then fails when an
issuer is projected into another tenant or takes its tenant from a claim; an issuer
without a tenant may remain. A resolver of the application's own must return that
tenant or none, because startup cannot inspect it.

For the legacy active-division flow, configure one existing
active-division authorization policy and explicit capability-to-policy mappings.
Set a bounded lowercase tool-invocation purpose with
`UseToolInvocationPurpose`. The registration supplies the production
`INhAiToolInvocationGate`: it uses the same per-request authenticated resolver,
re-authorizes descriptor policies, builds context through the shared factory,
and accepts only a bounded idempotency header.
The contributor recognizes a context of the request's own person, calls
`IAuthorizationService`, and only then copies the division ID into a bounded
execution scope and grants a lowercase dash-case capability. The scope says
which division the call is authorized for, not which objects its arguments
name: a tool that acts on an object it receives by id loads that object within
the scope's division and refuses it otherwise before any side effect, as the
sample's project tools do (SPM-314).

The platform recognizes the person who sent a request by the platform's actor id,
never by a claim: the id the registered `INhAiAuthenticatedInvocationContextResolver`
resolves for the request, resolved at most once per request. It is the same id the
assistant records as a conversation's owner, a run's initiator and a consultation's
asker. The contributor, the agent gate that reuses an initiator's live request
principal in a request turn, and the consultation tools that reuse the asker's all
compare with it. NewHeap Identity hosts resolve the `NameIdentifier` claim, so for
them the two are the same; an issuer projection (`oidc:` ids) or an application with
its own identity model, whose resolver maps people to its own actor ids (for example
through its `INhActorContextAccessor`), is recognized as well. Register such a
resolver with `UseAuthenticatedInvocationContextResolver<TResolver>()` and return the
application's actor id as `NhAiInvocationContext.ActorId`.

Agent runs have no HTTP request. Inside an agent invocation scope the same gate
first tries delegation: when the agent may act for its initiator, it builds the
initiator's principal through `INhAiPrincipalFactory` (by default from the
NewHeap user manager's current claims, so a removed or blocked user has no
rights) and authorizes the tool policies against an
`NhAiSubjectAuthorizationResource` for the subject's division. Otherwise it
checks the agent's two-key grant. The active-division handler accepts any
`INhDivisionScopedResource`: the resource's division wins over the request
header, and without both a request and a resource the requirement does not
succeed. Discovery inside an agent scope uses the same decision through
`INhAiAgentToolAuthorizer`, so the model is offered exactly the tools it may call.

When people's rights come from a claims transformation per request, as in an OIDC host
that adds the application's own permissions, the user store cannot rebuild them. Implement
`INhAiAccessSource.GetAsync(actorId, subject)`, which returns an `NhAiAccessContext` with the
claims the application last knew and when they were valid (`ValidAt`), or `null`, and call
`UsePrincipalsFromAccessSource<TSource>(o => o.MaxAge = ...)`. It replaces the built-in factory,
so every decision outside a person's request gets these claims: runs that delegate from their
initiator and their follow-up runs, consultations, the account an agent answers to, MCP
connections in the background and the whole application's people. `MaxAge` is required and
positive (there is no default; zero or a negative value fails at startup) and is measured on the
application's clock, the registered `TimeProvider`; date `ValidAt` on the same clock. No
context, a context older than `MaxAge`, or one dated further ahead than `MaxAge` yields no
principal, so the work is refused with the usual codes (`ai-tool-agent-not-granted`) and the
tool is not offered, until the person is back. The principal is one authenticated identity of
type `nh-ai-delegation` with exactly the context's claims: NewHeap adds no actor id, tenant or
right, so key the contexts by the platform's actor id that the registered resolver resolves in
the person's request (an `oidc:` id for an issuer projection, not the `sub` claim), and record
the claims the application's policies read. Where the integration takes tenants from a claim
(`UseAuthenticatedClaims`), a subject of a tenant needs exactly one tenant claim with that tenant;
with one fixed tenant (`UseAuthenticatedClaimsForSingleTenant`, the single-tenant mode) no tenant
claim is read. Set `NameClaimType` and `RoleClaimType` to the host's when inbound claim mapping is
off. NewHeap stores nothing: the application decides whether and where it keeps contexts (next
to its sessions, a distributed cache), forgets them at sign-out and answers for technical
accounts too, typically from live data with `ValidAt` now. A principal factory of the host's own
cannot be combined with it: one registered before the call fails it, one registered after fails
the host's start. The MCP owner sweep removes no connection on the source's answer, because a
missing or stale context does not mean that the person was removed.

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
- Authorizing a headless agent call with a cached principal or with the request header of whoever triggered the run.
- Deciding that a request is a person's own by comparing a claim such as `NameIdentifier` with an actor id; compare with the actor id the registered resolver resolves.
- Pairing `UseSingleTenant` with a tenant-claim projection or with another fixed tenant; startup refuses it, and a host with many tenants keeps the whole application's work for callers without one.
- Writing an `INhAiPrincipalFactory` that caches a person's claims without a bound, or combining one with `UsePrincipalsFromAccessSource`; return the claims from an `INhAiAccessSource` with a `MaxAge`.
- Adding rights, an actor id or a tenant to an access context that the person's request did not carry, or keying contexts by the `sub` claim where the actor id is an issuer's `oidc:` id.

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
With a resolver whose actor ids differ from the claim, verify that the request's own
person gets the scope, capabilities and correlation id while another person in the
same request gets none, that a request turn reuses the initiator's live principal and
refuses a different person, and that a consultation in the asker's own request offers
the consulted agent the tools the asker may use.
With `UseSingleTenant`, verify that startup refuses a projection of another tenant
and a tenant claim, and that a person projected into the tenant sees the whole
application's work.
With `UsePrincipalsFromAccessSource`, verify with a test clock that a fresh context lends
the initiator's and the accountable account's rights, that no context, a context older
than `MaxAge` and a context without the tool's policy are refused, and that startup refuses
a missing `MaxAge` and a principal factory of the host's own.
SPM-223 is the executable reference; SPM-302 shows the single-tenant deployment and SPM-315
the access source.
