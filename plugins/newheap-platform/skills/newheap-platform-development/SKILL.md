---
name: newheap-platform-development
description: Build, bootstrap, upgrade, test or review applications that consume NewHeap Platform packages. Routes each request to a focused internal foundation, backend, frontend, authentication, database, media, background-processing, runtime-configuration or testing module. Excludes maintenance of the NewHeap Platform libraries themselves.
---

# NewHeap Platform development

Route the current consumer task to the smallest applicable internal module. Read that module's `SKILL.md` completely before acting:

- [foundation](skills/foundation/SKILL.md) for bootstrapping, inspecting, repairing or upgrading a consumer repository;
- [backend](skills/backend/SKILL.md) for .NET foundations, modules, models, services, controllers, Scalar contracts, partial updates and units of work;
- [frontend](skills/frontend/SKILL.md) for Angular configuration, collections, lifecycle behavior and modals;
- [authentication](skills/authentication/SKILL.md) for roles, claims, policies, resource permissions and authentication overrides;
- [database](skills/database/SKILL.md) for provider-neutral queries plus SQL Server and PostgreSQL behavior;
- [media](skills/media/SKILL.md) for media contracts, storage, authorization, uploads and events;
- [background processing](skills/background-processing/SKILL.md) for transactional events, jobs, consumers and notifications;
- [runtime configuration](skills/runtime-configuration/SKILL.md) for appsettings, environment, secrets-path and command-line precedence;
- [testing](skills/testing/SKILL.md) for reusable test helpers and NewHeap consumer test boundaries.

Read multiple modules only when the requested work genuinely crosses their boundaries. Keep the router itself free of domain instructions; the focused modules and their references are the authoritative guidance.

For changes to the reusable NewHeap libraries, use the separate `newheap-library-maintenance` skill instead.
