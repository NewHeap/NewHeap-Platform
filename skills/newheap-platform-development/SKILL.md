---
name: newheap-platform-development
description: Build, bootstrap, upgrade, test or review applications that consume NewHeap Platform packages. Routes each request to a focused internal foundation, backend, frontend, authentication, database, media, background-processing, runtime-configuration or testing module. Excludes maintenance of the NewHeap Platform libraries themselves.
---

# NewHeap Platform development

Route the current consumer task to the smallest applicable internal module. Read that module's `SKILL.md` completely before acting:

- [foundation](../newheap-consumer-development/SKILL.md) for bootstrapping, inspecting, repairing or upgrading a consumer repository;
- [backend](../newheap-backend-development/SKILL.md) for .NET foundations, modules, models, services, controllers, Scalar contracts, partial updates and units of work;
- [frontend](../newheap-frontend-development/SKILL.md) for Angular configuration, collections, lifecycle behavior and modals;
- [authentication](../newheap-authentication/SKILL.md) for roles, claims, policies, resource permissions and authentication overrides;
- [database](../newheap-database-development/SKILL.md) for provider-neutral queries plus SQL Server and PostgreSQL behavior;
- [media](../newheap-media-development/SKILL.md) for media contracts, storage, authorization, uploads and events;
- [background processing](../newheap-background-processing/SKILL.md) for transactional events, jobs, consumers and notifications;
- [runtime configuration](../newheap-runtime-configuration/SKILL.md) for appsettings, environment, secrets-path and command-line precedence;
- [testing](../newheap-testing/SKILL.md) for reusable test helpers and NewHeap consumer test boundaries.

Read multiple modules only when the requested work genuinely crosses their boundaries. Keep the router itself free of domain instructions; the focused modules and their references are the authoritative guidance.

For changes to the reusable NewHeap libraries, use the separate `newheap-library-maintenance` skill instead.
