---
id: nh-auth-permission-scopes
title: "Application, division, and resource permissions"
area: authentication
reference: authorization-permissions
summary: "Model permissions as three explicit scopes and prove the complete chain from seed and claim to backend policy and frontend visibility."
sample-cases: ["SPM-071", "SPM-072", "SPM-073", "SPM-074", "SPM-137", "SPM-138", "SPM-210", "SPM-211", "SPM-212"]
public-symbols: ["ProtectedNhBaseController", "NhDivisionRoleClaim", "NhUserManager"]
skills: ["newheap-authentication"]
providers: ["provider-neutral"]
risk: critical
---
## Preferred approach

Keep application permissions, active-division permissions, and consumer-specific resource permissions as separate scopes. Treat `NhDivision` as the tenant or organizational boundary in a multi-tenant consumer: division membership, role claims, active-division selection and every division-owned resource must agree. Seed multiple roles with demonstrably different rights. Enforce the permission in the backend and use the same claim or policy for frontend visibility; frontend guards are never the security boundary.

Keep consumer-specific claim types, requirements, and handlers in the consumer. Encode the resource ID in the claim value, verify that the resource belongs to the active division, and implement an explicit hierarchy when application or division permissions may grant access.

## Avoid

- Hiding only a menu item without a backend policy.
- Trusting resource claims without checking the active division.
- Putting consumer-specific permissions in the generic library.
- Using a single administrator role as the only authorization example.

## Verification

Test one allowed and one denied combination for every scope, switch the active division, and try a resource from another division. Verify both HTTP status and frontend visibility for every seeded role.
