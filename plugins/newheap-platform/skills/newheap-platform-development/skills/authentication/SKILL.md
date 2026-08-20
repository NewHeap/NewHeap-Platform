---
name: newheap-authentication
description: Implement or review NewHeap consumer authentication and authorization, including token customization, roles, active divisions, resource permissions, claim hydration and removed-user handling.
---

# NewHeap authentication

Keep consumer-specific identity and resource rules in the consuming application while preserving NewHeap's standard endpoints, cookies and refresh-token behavior unless the requested protocol explicitly changes them.

Read [authentication overrides](references/authentication-overrides.md) for token customization, claim hydration or removed-user behavior. Read [authorization permissions](references/authorization-permissions.md) for application, division or resource scopes. Read both only when the task crosses those boundaries.

Preserve the complete authorization chain: seeded role or claim, token or claim hydration, backend policy enforcement and frontend visibility. Application, division and resource permissions are separate scopes. Resource claims encode the resource identity and validate that it belongs to the active division.

For large or volatile claims, keep stable application claims in the token and restore current claims through `IClaimsTransformation`. Cache per request, deduplicate claims and make a valid token for a removed user produce `401`.

Verify backend enforcement independently from frontend visibility and report the claim scopes and denial paths exercised.
