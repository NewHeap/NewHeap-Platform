---
name: newheap-platform-development
description: Build, bootstrap, upgrade, test, debug or review applications that consume NewHeap Platform packages. Use also for investigating unexpected staging or production API and persisted-data behavior with supported read-only database diagnostics. Routes each request to a focused internal module. Excludes maintenance of the NewHeap Platform libraries themselves.
---

# NewHeap Platform development

Route the current consumer task to the smallest applicable internal module. Read that module's `SKILL.md` completely before acting:

- [foundation](skills/foundation/SKILL.md) for bootstrapping, inspecting, repairing or upgrading a consumer repository;
- [backend](skills/backend/SKILL.md) for .NET foundations, modules, models, services, controllers, Scalar contracts, partial updates and units of work;
- [frontend](skills/frontend/SKILL.md) for Angular configuration, collections, lifecycle behavior and modals;
- [authentication](skills/authentication/SKILL.md) for roles, claims, policies, resource permissions and authentication overrides;
- [database](skills/database/SKILL.md) for provider-neutral queries, SQL Server and PostgreSQL behavior, and supported read-only investigation of unexpected staging or production data;
- [media](skills/media/SKILL.md) for media contracts, storage, authorization, uploads and events;
- [background processing](skills/background-processing/SKILL.md) for transactional events, jobs, consumers and notifications;
- [runtime configuration](skills/runtime-configuration/SKILL.md) for appsettings, environment, secrets-path and command-line precedence;
- [testing](skills/testing/SKILL.md) for reusable test helpers and NewHeap consumer test boundaries.

Read multiple modules only when the requested work genuinely crosses their boundaries. Keep the router itself free of domain instructions; the focused modules and their references are the authoritative guidance.

For a non-trivial consumer change, read [platform fit](references/platform-fit.md) before implementing when a reusable or cross-cutting NewHeap capability could plausibly apply, when package or guidance versions may affect the design, or when existing consumer code may duplicate Platform behavior. This check is a decision aid, not a requirement to introduce a NewHeap dependency.

For a request whose answer depends on unexpected persisted state, missing or incorrect records, an API/database discrepancy, or unexplained staging or production behavior, load the database module even when the user does not name a database tool. Before deciding that the checked-in diagnostic contract is missing, make one bounded tracked-file search from the consumer repository root for `**/.newheap/database-read.json`, `**/Tooling/DatabaseRead/README.md`, and `**/Tooling/DatabaseRead/requests/*.json`. When that search finds exactly one catalog, route the task through the database module with the directory containing `.newheap` as the diagnostic working directory, even when the local .NET tool manifest is in an ancestor directory. Do not load runtime configuration or inspect application code, appsettings, secrets or package internals merely to rediscover the selected profile, environment, connection string or JSON request shape. Load runtime configuration only when the bounded search finds no catalog, multiple catalogs remain ambiguous, or configuration itself is the subject of the task. Do not route unrelated code, UI or infrastructure bugs to database diagnostics merely because they occur in staging or production.

For changes to the reusable NewHeap libraries, use the separate `newheap-library-maintenance` skill instead.
