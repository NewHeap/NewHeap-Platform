---
name: newheap-background-processing
description: Implement or review NewHeap consumer CAP events, transactional outbox behavior, idempotent consumers, background jobs and operations, mail and notification delivery, and AI work that runs in the background. Excludes unrelated runtime configuration and media storage.
---

# NewHeap background processing

Read [background processing](references/background-processing.md) before changing events, jobs, mail or notifications.

For an event coupled to a mutation, also read [service-owned unit of work](references/backend-unit-of-work.md) so publication participates in the correct transaction boundary.

Read [operation suspension](references/background-operation-suspension.md) when a durable background operation must wait for approval or other authorized external input. Read [modules without NewHeap Identity](references/platform-modules-without-identity.md) when the application hosts background operations or notifications in its own EF context and answers the platform's actor, division, directory, notification and retention questions itself.

Tie event publication to the service-owned transaction when atomic delivery is required. Consumers and jobs must tolerate retries and duplicate delivery. Singleton hosted services create a scope per iteration rather than retaining scoped state.

Keep notification creation, delivery channels and dispatch separate. Configure dispatcher concurrency only for idempotent channels that may run in parallel, and restart the processor after changing worker counts.

Verify commit and rollback, duplicate delivery, retry, worker capacity, stale recovery and provider-specific locking on every relational provider the consumer supports. Report any delivery ordering assumption explicitly.

## AI work in the background

Read these references only when background work runs AI, and only those whose boundaries the task crosses:

- [durable runs](references/ai-durable-runs.md) when AI work outlives a request inside a background operation, with checkpoints and approval waits;
- [agent runs](references/ai-agent-runs.md) when agents start from committed events, schedules or people and run headless as background operations;
- [Agent Framework](references/ai-agent-framework.md) when such work creates Microsoft Agent Framework agents bound to NewHeap profiles, tools and policy;
- [ingestion](references/ai-ingestion.md) when content is indexed into a vector store in the background with scope, provenance and classification.

The models, tools and assistant these runs use belong to the backend module.
