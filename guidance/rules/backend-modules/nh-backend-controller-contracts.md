---
id: nh-backend-controller-contracts
title: "Controllers and Scalar as an executable contract"
area: backend
reference: backend-controller-contracts
summary: "Make routing, binding, authorization, and response metadata explicit for every action so Scalar can display and test the real contract."
sample-cases: ["SPM-001", "SPM-009", "SPM-189"]
public-symbols: ["ProtectedNhBaseController", "DbEntityProtectedNhBaseController", "PublicNhBaseController"]
skills: ["newheap-backend-development"]
providers: ["provider-neutral"]
risk: medium
---
## Preferred approach

Give every HTTP action a concise `EndpointSummary`, a useful `EndpointDescription`, and explicit `ProducesResponseType` metadata for success and the errors the implementation can actually return. Use typed response models, make route, query, body, form, and service binding explicit, and deliberately place `Authorize`, a policy, or `AllowAnonymous` on the action or controller.

Use the appropriate NewHeap base controller when it covers the contract, but keep a thin consumer controller for domain-specific orchestration. Verify `/openapi/v1.json` and `/scalar` as part of the change.

## Avoid

- Anonymous objects as a public response contract.
- Documenting status codes that have no execution path.
- Making authorization depend on an implicit global assumption.
- Treating Scalar as cosmetic documentation; its metadata is part of the API contract.

## Verification

Run the reflection-based controller contract test and open both the OpenAPI document and Scalar. Test at least one success path, unauthorized access, and relevant validation or not-found paths.
