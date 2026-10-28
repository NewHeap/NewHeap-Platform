---
id: nh-auth-permission-scopes
title: "Application, division, and resource permissions"
area: authentication
reference: authorization-permissions
summary: "Model permissions as three explicit scopes and prove the complete chain from seed and claim to backend policy and frontend visibility."
sample-cases: ["SPM-071", "SPM-072", "SPM-073", "SPM-074", "SPM-137", "SPM-138", "SPM-210", "SPM-211", "SPM-212"]
public-symbols: ["ProtectedNhBaseController", "NhDivisionRoleClaim", "NhUserManager", "NhBaseDivisionUserController", "NhBaseDivisionController"]
skills: ["newheap-authentication"]
providers: ["provider-neutral"]
risk: critical
---
## Preferred approach

Keep application permissions, active-division permissions, and consumer-specific resource permissions as separate scopes. Treat `NhDivision` as the tenant or organizational boundary in a multi-tenant consumer: division membership, role claims, active-division selection and every division-owned resource must agree. Seed multiple roles with demonstrably different rights. Enforce the permission in the backend and use the same claim or policy for frontend visibility; frontend guards are never the security boundary.

Every division decision requires membership of the requested division, also without a required claim or role: `DivisionAccessAsync`, `HasDivisionAccessAsync`, NewHeap Identity's `INhDivisionAccess` and `RequireActiveDivisionAccess()` deny a user who is no member, and a division id that does not exist. NewHeap Identity adds a `NhPlatformClaimTypes.DivisionMember` claim for every active membership, also without division roles; keep it with the other division claims when an application moves them out of the token.

Manage division memberships with a controller derived from `NhBaseDivisionUserController` and register its `app.division.view` and `app.division.manage` policies as division policies (`RequireActiveDivisionAccess` with a division permission), so a division manager works in the division of the active-division header. Without the division access-all permission, the inherited writes stay in that division: a membership of another division returns `404`, a body that names another division returns `403`, and `NhBaseDivisionController` adds divisions only for the access-all permission. Every inherited update and partial update resolves its row through the controller's `GetQueryableAsync` first, as delete does, so a scope filter there bounds writes as well as reads. Suspend a member with the lockout window of the membership: from the day `LockOutStartDateTime` is reached until the day `LockOutEndDateTime` has passed, or indefinitely without an end date, NewHeap Identity adds no claims for that division, and the per-request reload of division policies denies at once. The window counts in whole UTC days of the application's clock (a registered `TimeProvider`, otherwise the system clock), from the start day through the end day, whatever offset a date was stored with: a lockout that ends at midnight UTC still applies that day. A user without an active division or memberships signs in with the application claims and no division claims.

Keep consumer-specific claim types, requirements, and handlers in the consumer. Encode the resource ID in the claim value, verify that the resource belongs to the active division, and implement an explicit hierarchy when application or division permissions may grant access.

## Avoid

- Hiding only a menu item without a backend policy.
- Trusting resource claims without checking the active division.
- A custom write action that loads its row by id alone; resolve it through `GetQueryableAsync` first, as the inherited writes do.
- Registering `app.division.manage` as an application-wide permission for managers who should manage one division only.
- Building division claims for authorization without the membership claim or a division role; a division check without requirements then denies the member.
- Comparing lockout dates by their local date (`.Value.Date`) in a query: SQL Server takes the day of the stored offset, and `.Value` throws for an empty date wherever the predicate runs in memory. Compare instants with the start of today and tomorrow in UTC, as NewHeap Identity does.
- Putting consumer-specific permissions in the generic library.
- Using a single administrator role as the only authorization example.

## Verification

Test one allowed and one denied combination for every scope, switch the active division, and try a resource from another division, including creating, updating and deleting a membership of another division, and a member whose membership is locked out next to members who are not. Test the lockout day edges (starting today and tomorrow, ending today and yesterday) on a test clock registered as the `TimeProvider`, and a user without an active division or memberships, on SQL Server and PostgreSQL; an EF Core InMemory test runs the claims path but is no evidence of their translation. Verify both HTTP status and frontend visibility for every seeded role.
