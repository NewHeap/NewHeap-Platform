---
name: newheap-backend-development
description: Build or change NewHeap consumer .NET foundations, modules, models, services, controllers, Scalar/OpenAPI contracts, partial updates, service-owned units of work, bulk imports, logging, and governed AI assistants, agents and tools. Excludes provider-specific database design and authentication policy work.
---

# NewHeap backend development

Keep domain entities, concrete services, DbContext configuration and migrations in the consuming application. Keep controllers thin and HTTP-specific; services own validation, normalization, query composition and transaction boundaries.

Read only the reference needed for the current backend task:

- [project foundation](references/backend-project-foundation.md) for solutions, projects and central build/package policy;
- [module composition](references/backend-module-composition.md) for entities, relationships, repositories and registration;
- [platform pipeline](references/backend-platform-pipeline.md) for `AddNewHeapPlatformAspNetCommon`, `UseNewHeapPlatformAspNetCommon` and controller routing;
- [models and localization](references/backend-models-localization.md) for view/mutate contracts and mappings;
- [controller contracts](references/backend-controller-contracts.md) for controllers and Scalar/OpenAPI;
- [partial updates](references/backend-partial-update.md) for top-level JSON patch behavior;
- [unit of work](references/backend-unit-of-work.md) for service-owned transactions;
- [collection processing](references/backend-collection-processing.md) for server-side filtering, ordering, paging defaults and bounded deadlock retries;
- [bulk upsert](references/database-bulk-upsert.md) for large untracked imports through `ExecuteUpsertAsync`;
- [logging and observability](references/observability-logging.md) for structured logging, OpenTelemetry traces and metrics, and correlation identifiers.

Read multiple references only when the requested change genuinely crosses those boundaries.

New modules normally include the consumer-owned entity and relationships, `DbSet`, repository and service registration, view and mutate models, NewHeap mapping profiles and a thin authorized controller. View-model IDs are filterable; mutate models omit creation and last-modified timestamps.

Every changed HTTP action exposes a typed Scalar/OpenAPI contract with summary, description, explicit binding, authorization intent and the response metadata the implementation can actually produce.

Use service-owned units of work for mutations and preserve the normal update pipeline for partial updates. Never hand-edit existing migrations or snapshots.

Run focused backend tests and the consumer's normal build. Report the contracts and transaction behavior exercised.

## AI assistants, agents and tools

Read these references only when the task adds or changes AI behavior through the `NewHeap.Platform.AI` packages, and only those whose boundaries it crosses:

- [model profiles](references/ai-model-profiles.md) when registering provider clients as named profiles with capabilities, data policy, budgets and startup requirements;
- [local tools](references/ai-local-tools.md) when declaring application tool contracts that become authorized local AI functions;
- [API bridge](references/ai-api-bridge.md) when existing authorized controller actions should become AI tools that run as the calling user;
- [context retrieval](references/ai-context-retrieval.md) when a model receives application context that must be authorized, scoped and budgeted;
- [ingestion](references/ai-ingestion.md) when content is indexed into a vector store with scope, provenance and classification;
- [protected actions](references/ai-protected-actions.md) when an AI action changes state and needs an exact proposal, approval and verification;
- [assets](references/ai-assets.md) when versioning instructions or agent assets for approvals, evaluation and rollout;
- [assistant](references/ai-assistant.md) for the in-app assistant: durable conversations, streamed turns, in-chat approvals, sharing and its administration;
- [agent runs](references/ai-agent-runs.md) for agents that run on their own from events, schedules or people, with deferred proposals, skills, boards and the approval inbox;
- [Agent Framework](references/ai-agent-framework.md) when creating Microsoft Agent Framework agents bound to NewHeap profiles, tools and policy;
- [durable runs](references/ai-durable-runs.md) when AI work outlives a request inside a background operation with checkpoints and approval waits;
- [MCP tools](references/ai-mcp-tools.md) when exposing generated tools to MCP clients;
- [MCP imports](references/ai-mcp-imports.md) when importing tools from external MCP servers;
- [observability and evaluation](references/ai-observability-evaluation.md) for content-free AI telemetry, usage and evaluation gates before changing production defaults.

The authenticated invocation context these features run under belongs to the authentication module.
