---
id: nh-operations-events-jobs-notifications
title: "Organize events, jobs, and notifications transactionally"
area: operations
reference: operations
summary: "Tie event publication to the unit of work, make consumers idempotent, and keep hosted services free of directly injected scoped state."
sample-cases: ["SPM-076", "SPM-077", "SPM-078", "SPM-079", "SPM-080", "SPM-081", "SPM-082", "SPM-083", "SPM-084", "SPM-085", "SPM-086", "SPM-087", "SPM-088", "SPM-089", "SPM-090", "SPM-193", "SPM-195", "SPM-199", "SPM-208"]
public-symbols: ["CapTransactionScope", "INhNotificationDispatcher", "NhHangfireUtil", "NhMailService", "NhNotificationSettings"]
skills: ["newheap-consumer-development"]
providers: ["sql-server", "postgresql"]
risk: critical
---
## Preferred approach

Publish CAP events inside the service-owned transactional scope and configure the outbox and broker explicitly. Give consumers a stable group and topic, make processing idempotent, and ensure a retry safely produces the same result. Keep background jobs small and repeatable. A singleton hosted service creates a scope for each iteration and resolves scoped services inside it.

Model notification creation, the delivery channel, and email dispatch as separate steps. Use typed events and templates, and store read and unread state in the consumer-owned database with appropriate migrations.

Notification dispatcher channels are serial by default. Opt a channel into parallel processing with `NhNotificationSettings.ProcessingDispatcherConcurrency[dispatcherId]` only when its dispatcher is safe to run concurrently. Worker counts are created when the notification processor starts, so restart the host after changing this setting. The processor claims a delivery only when a worker is available, records the attempt before calling the dispatcher, and ignores a late result when a newer attempt has already claimed the delivery. Keep dispatchers idempotent because stale recovery and retries provide at-least-once delivery.

## Avoid

- Injecting `CapTransactionScope` directly into a singleton `IHostedService`.
- Publishing to the broker separately from the database commit when atomic delivery is required.
- A retry-sensitive consumer without an idempotency key or processing record.
- Handling email or push synchronously in the HTTP controller.
- Assuming delivery order is preserved after configuring more than one worker for a dispatcher channel.
- Sharing one dispatcher ID across workloads that require different ordering or concurrency guarantees.

## Verification

Test successful processing, retry, duplicate delivery, publication failure, rollback, job deduplication, and notification state. For notification dispatchers, block one attempt deliberately and verify queued deliveries remain queued until worker capacity exists, attempts are recorded before dispatch, configured concurrency removes head-of-line blocking, and stale results cannot overwrite newer attempts. Run claim and transaction-lock tests on SQL Server and PostgreSQL, and verify scoped DI validation at host startup.
