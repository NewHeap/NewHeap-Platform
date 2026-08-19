---
name: newheap-consumer-development
description: Scope, bootstrap, build and change applications that consume NewHeap Platform libraries. Use for turning plain-language product needs into a compact service, API or management-portal foundation; an empty consumer repository or package/plugin URL; incremental consumer repository and .NET solution scaffolding, public package installation, central build and package files, post-setup validation, management portals, backend modules, controllers, services, entities, models, EF Core and migrations; Angular pages, collections, modals, forms, dropdowns, interceptors and root configuration; authentication, roles, divisions, resource permissions and claim overrides; SQL Server/PostgreSQL behavior; optional Aspire, Docker and Elasticsearch integration; or when diagnosing or upgrading a consumer.
---

# NewHeap consumer development

Use the public library surface and the executable SampleProjectManagement cases as the contract. Match the consuming application's established composition style, but use the current preferred NewHeap API where the guidance says so.

## Start with evidence

1. Locate the repository root and its `AGENTS.md` files. More specific instructions win.
2. Inspect an existing consumer before editing. Run `node <skill-directory>/scripts/inspect-newheap-consumer.mjs <consumer-root>` for a quick inventory. For an empty repository, follow the bootstrap route below instead.
3. Determine the installed NewHeap package versions, target framework, Angular version, database providers and existing registration style.
4. Read only the relevant reference below, then open the linked executable sample evidence before implementing.
5. Inspect two or three nearby consumer implementations where conventions are application-owned.

## Route the task

- Empty repository, package/plugin URL, first-time setup, management-portal foundation, package gate, Aspire, Docker, Elasticsearch or post-setup audit: [consumer bootstrap](references/consumer-bootstrap.md)
- Existing .NET solution scaffolding, central MSBuild/package policy, backend module, models, service, controller, Scalar or unit of work: [backend modules](references/backend-modules.md)
- Angular collection, filter, lifecycle, modal, dropdown or root config: [frontend components](references/frontend-components.md)
- Login, token, role, division, claim or resource permission: [authentication](references/authentication.md)
- EF Core, migration, query translation, raw SQL or provider wiring: [database providers](references/database-providers.md)
- CAP, jobs, mail, notifications or media: [operations](references/operations.md)
- Reusable test contexts, assertions, substitutes or test-project setup: [testing](references/testing.md)
- Public package installation, source cleanup, upgrades or the distributed AI skill: [package sources](references/package-sources.md)

If a task crosses subjects, read each applicable reference. Do not load unrelated reference files.

## Confirm scope before scaffolding

Infer as much as possible from the request, repository and established organizational choices. Ask only for missing information that changes the structure. Use short product-language questions, no more than three at a time:

- What must the first useful version let someone or something accomplish?
- Who or what uses it: people through screens, another system, or an automatic process?
- Must it retain information that can be found again later?
- Must people sign in or may different people do different things?
- Is a visible interface needed now, or may this increment work without screens?
- Which existing systems or operating constraints must it connect to?

Do not ask a non-technical user to choose an API, worker, Angular, authentication middleware or database engine. Translate the answers internally: automatic work to `service`, system-to-system requests to `api`, and confirmed interactive administration to `management-portal`. Select persistence, authentication and optional infrastructure only when the outcome requires them. Use an established repository standard without reopening the choice.

Before generating files, summarize the proposed result in plain language and name what stays deferred. Keep future capabilities as extension seams, not installed frameworks. A backend-only profile keeps `src/Front-end/.gitkeep` and no Angular or npm workspace. When a later request confirms a frontend, treat it as an additive scope change: preserve existing backend hosts, add an API only if the interface needs one, update the manifest capabilities, and then materialize the Angular workspace from the executable frontend evidence. Read [consumer bootstrap](references/consumer-bootstrap.md) for the complete decision and verification rules.

## Empty repository gate

When the repository has no consumer application yet:

1. Install this versioned skill from the supplied NewHeap plugin/package artifact. Never execute an unverified URL or put credentials in the URL.
2. Complete the scope gate and run `node <skill-directory>/scripts/bootstrap-newheap-consumer.mjs <consumer-root> --name <Application.Name> --profile <service|api|management-portal> --database <none|postgresql|sqlserver>`. Add `--authentication` for a protected non-portal API. Add `--aspire`, `--docker` or `--elasticsearch` only for confirmed needs.
3. Let the script restore NuGet and, only for a management portal, npm packages anonymously from the public registries. If restore fails, stop feature work, remove stale private-feed overrides or fix connectivity as described in [package sources](references/package-sources.md), and rerun the bootstrap. Use `--skip-install` only for offline structure tests, never as proof of a ready project.
4. Require `inspect-newheap-consumer.mjs <consumer-root> --mode foundation` to pass before generating the selected identity, domain, API, service or UI capabilities.
5. Build only the confirmed capabilities from linked executable sample evidence. For a management portal, do not use the generic Angular starter, plain controller/API wrappers, or an edit aside as substitutes for NewHeap patterns. If no frontend is confirmed, preserve the placeholder and do not create frontend configuration.
6. Run the normal builds and `inspect-newheap-consumer.mjs <consumer-root> --mode validate` before declaring setup complete.

## Implement in consumer-owned seams

- Write consumer documentation and AI-facing instructions in English. Every
  user-facing sample or application must provide a complete English translation
  set; additional languages may remain when their key sets stay aligned with
  English.
- Keep domain entities, resource permissions, concrete services, DbContext configuration and migrations in the consuming implementation.
- Keep controllers thin and HTTP-specific; put validation, normalization, query composition and transaction ownership in concrete services.
- Prefer the documented fluent and lifecycle extension points over raw construction or Angular hooks owned by NewHeap bases.
- Register provider choice in the composition layer. Treat SQL Server and PostgreSQL as the default relational support matrix.
- Preserve NewHeap's authentication endpoints, shell lifetime and module provider scopes unless the task explicitly changes the protocol.

Never compensate for a consumer issue by silently changing a library default. In particular, recommended GET deduplication and deferred dropdown behavior are explicit consumer opt-ins and remain disabled by default in the library for compatibility.

## Verify the result

Run the smallest focused tests plus the consumer's normal profile-specific build. Do not require npm or browser checks from a backend-only profile. For database behavior, use real SQL Server and PostgreSQL integration evidence where query translation, migrations, constraints, raw SQL or transactions matter; InMemory is not equivalent. For Angular changes, build and inspect desktop and narrow mobile UI, console errors, navigation, loading/empty/error states and horizontal overflow.

Report:

- the NewHeap rule(s) and executable sample case(s) followed;
- the consumer-owned artifacts changed;
- the provider/browser matrix actually exercised;
- any unverified provider or genuine library gap.

When the public NewHeap surface itself must change, do not invent a consumer workaround. Move that work to a NewHeap Platform checkout, where `$newheap-library-maintenance` owns the library, sample and release change.
