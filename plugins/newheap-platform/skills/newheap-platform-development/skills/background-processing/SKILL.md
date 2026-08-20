---
name: newheap-background-processing
description: Implement or review NewHeap consumer CAP events, transactional outbox behavior, idempotent consumers, background jobs, mail and notification delivery. Excludes unrelated runtime configuration and media storage.
---

# NewHeap background processing

Read [background processing](references/background-processing.md) before changing events, jobs, mail or notifications.

For an event coupled to a mutation, also read [service-owned unit of work](references/backend-unit-of-work.md) so publication participates in the correct transaction boundary.

Tie event publication to the service-owned transaction when atomic delivery is required. Consumers and jobs must tolerate retries and duplicate delivery. Singleton hosted services create a scope per iteration rather than retaining scoped state.

Keep notification creation, delivery channels and dispatch separate. Configure dispatcher concurrency only for idempotent channels that may run in parallel, and restart the processor after changing worker counts.

Verify commit and rollback, duplicate delivery, retry, worker capacity, stale recovery and provider-specific locking on every relational provider the consumer supports. Report any delivery ordering assumption explicitly.
