---
id: nh-consumer-management-portal
title: "Use a NewHeap management portal for confirmed interactive work"
area: consumer-bootstrap
reference: consumer-bootstrap
summary: "When the product scope requires interactive administration, use the authenticated, information-dense NewHeap management profile instead of a generic website starter."
sample-cases: ["SPM-001", "SPM-112", "SPM-115", "SPM-217"]
public-symbols: ["DbEntityProtectedNhBaseController", "NhBaseApiService", "NhModalMutateBaseComponent"]
skills: ["newheap-consumer-development"]
providers: ["provider-neutral"]
risk: high
---
## Preferred approach

When the confirmed first increment requires employees or customers to administer information through screens, generate the management-portal profile: authenticated shell and navigation, a protected collection, NewHeap API service, loading and error states, and create/edit mutations opened through `NhModalService`. Derive backend CRUD controllers from `DbEntityProtectedNhBaseController` or the applicable protected NewHeap base and add explicit authorization plus Scalar metadata. Derive frontend API clients from `NhBaseApiService`, use NewHeap collection and lifecycle bases, and derive mutating modal content from `NhModalMutateBaseComponent`.

Keep the browser API base at `/api`, strip that prefix in the Angular proxy, and expose backend routes without repeating `api/`. Configure `NhCommonModule.forRoot(...)` once in the application root, including the recommended GET deduplication and deferred dropdown opt-ins. Use the sample management application as executable evidence for shell, modal and collection behavior; the default Angular welcome page is not a NewHeap starter.

## Avoid

- Generating a portal before the product scope confirms an interactive interface.
- Generating a marketing site, generic dashboard template or unprotected Angular welcome page for an interactive management profile.
- Reimplementing CRUD actions on plain `ControllerBase` when a NewHeap protected/base controller owns the contract.
- Building raw `HttpClient` wrappers instead of `NhBaseApiService`.
- Editing an entity in an aside or drawer when the established NewHeap mutation flow is a modal.
- Overriding Angular lifecycle methods owned by a NewHeap base component.

## Verification

Run `inspect-newheap-consumer.mjs <consumer-root> --mode validate` and require evidence of one root `NhCommonModule.forRoot(...)`, a protected NewHeap controller, an `NhBaseApiService`, NewHeap modal content, `NhIdentityDbContext`, and the canonical proxy rewrite. Build the management application and inspect login, shell, navigation, collection loading/empty/error states and modal create/edit behavior at desktop and narrow widths.
