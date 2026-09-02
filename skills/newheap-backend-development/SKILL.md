---
name: newheap-backend-development
description: Build or change NewHeap consumer .NET foundations, modules, models, services, controllers, Scalar/OpenAPI contracts, partial updates and service-owned units of work. Excludes provider-specific database design and authentication policy work.
---

# NewHeap backend development

Keep domain entities, concrete services, DbContext configuration and migrations in the consuming application. Keep controllers thin and HTTP-specific; services own validation, normalization, query composition and transaction boundaries.

Read only the reference needed for the current backend task:

- [project foundation](references/backend-project-foundation.md) for solutions, projects and central build/package policy;
- [module composition](references/backend-module-composition.md) for entities, relationships, repositories and registration;
- [models and localization](references/backend-models-localization.md) for view/mutate contracts and mappings;
- [controller contracts](references/backend-controller-contracts.md) for controllers and Scalar/OpenAPI;
- [partial updates](references/backend-partial-update.md) for top-level JSON patch behavior;
- [unit of work](references/backend-unit-of-work.md) for service-owned transactions.

Read multiple references only when the requested change genuinely crosses those boundaries.

New modules normally include the consumer-owned entity and relationships, `DbSet`, repository and service registration, view and mutate models, NewHeap mapping profiles and a thin authorized controller. View-model IDs are filterable; mutate models omit creation and last-modified timestamps.

Every changed HTTP action exposes a typed Scalar/OpenAPI contract with summary, description, explicit binding, authorization intent and the response metadata the implementation can actually produce.

Use service-owned units of work for mutations and preserve the normal update pipeline for partial updates. Never hand-edit existing migrations or snapshots.

Run focused backend tests and the consumer's normal build. Report the contracts and transaction behavior exercised.
