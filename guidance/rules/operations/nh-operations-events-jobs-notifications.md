---
id: nh-operations-events-jobs-notifications
title: "Organize events, jobs, and notifications transactionally"
area: operations
reference: background-processing
summary: "Tie event publication to the unit of work, make consumers idempotent, and keep hosted services free of directly injected scoped state."
sample-cases: ["SPM-076", "SPM-077", "SPM-078", "SPM-079", "SPM-080", "SPM-081", "SPM-082", "SPM-083", "SPM-084", "SPM-085", "SPM-086", "SPM-087", "SPM-088", "SPM-089", "SPM-090", "SPM-193", "SPM-195", "SPM-199", "SPM-208"]
public-symbols: ["CapTransactionScope", "INhBackgroundOperationContext", "INhBackgroundOperationFanOutContext", "INhBackgroundOperationHandler<TRequest>", "INhBackgroundOperationLeaseManager", "INhBackgroundOperationNotificationFormatter", "INhNotificationDispatcher", "NhBackgroundOperationBuilder", "NhBackgroundOperationChangedMessage", "NhBackgroundOperationFanOut", "NhBackgroundOperationResourceKey", "NhBackgroundOperationRetryResult", "NhBackgroundOperationStore", "NhBackgroundOperationsOptions", "NhHangfireUtil", "NhMailService", "NhNotificationSettings", "TaskResult"]
skills: ["newheap-background-processing"]
providers: ["sql-server", "postgresql"]
risk: critical
---
## Preferred approach

Publish CAP events inside the service-owned transactional scope and configure the outbox and broker explicitly. Give consumers a stable group and topic, make processing idempotent, and ensure a retry safely produces the same result. Keep background jobs small and repeatable. A singleton hosted service creates a scope for each iteration and resolves scoped services inside it.

Model notification creation, the delivery channel, and email dispatch as separate steps. Use typed events and templates, and store read and unread state in the consumer-owned database with appropriate migrations.

Use `NhHangfireUtil` for small, naturally repeatable fire-and-forget work. Use `WithBackgroundOperations` plus a typed `INhBackgroundOperationHandler<TRequest>` when work needs durable ownership, cancellation, progress, retries, a result, or user-visible notification milestones. Register every payload type explicitly; payloads never carry CLR type metadata. Deploy the consumer DbContext migration before enabling enqueueing or workers.

Treat the registered payload schema version as durable data. Keep request changes backward compatible at the current version or introduce an explicit migration/upcaster before incrementing it. A queued row whose schema version no longer matches its handler fails terminally with a safe code instead of entering a retry loop.

Background-operation handlers return `Task<TaskResult>`. Return a failed `TaskResult` for an expected business outcome that should end without an automatic retry, propagate failed results from helpers, and use `NhBackgroundOperationRetryResult.Retry(...)` for a known transient outcome that may follow the registered retry policy. Throw only for cancellation, corrupt/fenced state, infrastructure faults, or genuinely unexpected handler failures. The runner keeps exception details in operator diagnostics and persists only safe failure codes and message keys.

Declare a weighted progress plan before visible work starts. A handler may report one overall range, run named phases, add translated nested substeps, or open a batch reporter inside any step. `RunStepAsync` returns the action's `TaskResult` and marks the step from that result, so callers must propagate a failed result. Batch reporters aggregate counters and flush at bounded intervals instead of creating one database row per item. `ItemFailedAsync` succeeds while the configured tolerance permits processing to continue and returns a failed result when the batch must stop. Keep progress writes short and monotonic, and use checkpoints for small recovery cursors rather than as an unbounded result store. Checkpoint compare-and-set conflicts and oversized checkpoint values are returned as `TaskResult` failures; validate and propagate the result from `SetAsync` and idempotent-step completion.

Use `context.FanOut.RunAsync(...)` when one parent must partition durable work across independently scheduled child operations. Register the child request and handler normally, then provide a stable fan-out key and stable item keys; NewHeap inherits owner, division, priority and correlation, resolves each child queue and concurrency policy, creates children idempotently, suspends the parent without occupying a worker or lease, aggregates child progress, and resumes the parent for fan-in. A final-child wake-up that encounters parent-lock contention receives a durable recheck at the next dispatcher interval rather than waiting for the longer reconciliation fallback. The parent handler is re-entered from its beginning after suspension, so preceding work must be repeatable or checkpointed. Repeated parent execution must provide the same item keys and payloads. Fan-in returns `TaskResult<NhBackgroundOperationFanOutResult>`; the default produces a failed result with child details after any unsuccessful child, while `NhBackgroundOperationFanOutFailureMode.Continue` returns a successful result containing the partial outcome. Always propagate or explicitly handle that result.

Cancelling a parent propagates durably through its descendants, while retrying a failed parent resets only unsuccessful descendants and preserves successful child results. Child milestones stay inside the parent progress experience instead of creating one user-notification thread per item. Keep fan-out items coarse enough to justify durable scheduling; use an in-operation batch reporter when individual items do not need their own retries, queue placement or result.

Choose idempotency deliberately. Require an enqueue idempotency key for `IdempotentWithKey`, use `NhBackgroundOperationResourceKey` helpers for deterministic per-user, per-division, or per-resource exclusivity, and configure retry count as zero for non-idempotent handlers. An internal completion checkpoint cannot atomically close the crash window around an external side effect: pass `NhBackgroundOperationIdempotentStep.ExternalIdempotencyKey` to the external system or use a transactional outbox. Acquire multiple runtime leases in deterministic order; the lease fencing token must be checked by any protected writer that can outlive its lease.

Configure global, queue, and operation-type concurrency separately. Registered operation queues are added to the Hangfire worker automatically; do not let request data choose an arbitrary queue. Use `ExclusivePer` for admission control and `AcquireRequiredAsync` or `AcquireManyRequiredAsync` for handler-local critical sections. Required-lease contention reschedules the operation without consuming a handler retry. NewHeap hashes persisted resource keys, but callers must still avoid putting secrets into key components.

Treat SignalR messages as authenticated invalidations, not authoritative state. Derive groups from server-side identity plus the validated active division, join global and accessible division scopes separately, refetch the protected HTTP snapshot, merge only newer versions, reconnect automatically, and retain polling as the canonical recovery path. A missing active division exposes global operations only. Clear the root Angular store immediately when the authenticated user or active division changes, discard late responses from the previous scope, and reconcile polling pages as snapshots while preserving explicitly watched details. Customize `INhBackgroundOperationNotificationFormatter` when milestone text must follow the operation owner's locale; notification formatting failure must never change the operation result.

Keep cleanup enabled with bounded batches and explicit payload, event, succeeded, cancelled, and failed retention periods. Apply the per-operation event target to every lifecycle and handler event, not only message events. Never trim an unprojected notification milestone: projection reconciliation is the durable hand-off, so a projector outage may temporarily put protected milestones above the target while ordinary and already projected events remain bounded. Payload redaction disables unsafe late retries, non-milestone events expire independently from the canonical snapshot, and lease tombstones retain monotonically increasing fencing tokens. Keep the operation-to-division foreign key restrictive so deleting a division cannot silently turn scoped history into global history. Monitor the `nh-background-operations` readiness check: degraded means stale attempts or overdue dispatch require reconciliation; unhealthy means persistence is unavailable.

Treat short transaction-lock contention inside a running operation as an internal scheduling outcome. Progress, message, checkpoint, result, fan-out, heartbeat, and completion writes unwind through the internal contention signal and reschedule without being reported as user cancellation or consuming a handler retry. Lost fencing remains exceptional and stops the stale attempt. Validate positive soft timeouts, progress flush intervals and transaction-lock timeouts during registration. Keep hub and notification paths as local application paths; reject protocol-relative paths, query strings, fragments and backslashes.

Notification dispatcher channels are serial by default. Opt a channel into parallel processing with `NhNotificationSettings.ProcessingDispatcherConcurrency[dispatcherId]` only when its dispatcher is safe to run concurrently. Worker counts are created when the notification processor starts, so restart the host after changing this setting. The processor claims a delivery only when a worker is available, records the attempt before calling the dispatcher, and ignores a late result when a newer attempt has already claimed the delivery. Keep dispatchers idempotent because stale recovery and retries provide at-least-once delivery.

## Avoid

- Injecting `CapTransactionScope` directly into a singleton `IHostedService`.
- Publishing to the broker separately from the database commit when atomic delivery is required.
- A retry-sensitive consumer without an idempotency key or processing record.
- Handling email or push synchronously in the HTTP controller.
- Assuming delivery order is preserved after configuring more than one worker for a dispatcher channel.
- Sharing one dispatcher ID across workloads that require different ordering or concurrency guarantees.
- Retrying a handler without documenting whether every side effect is idempotent.
- Holding an EF transaction open while the handler performs long-running work.
- Blocking a parent worker while polling child operations, or manually enqueueing children without stable item keys.
- Using an in-process semaphore as the only cross-node resource lock.
- Persisting raw exception text, secrets, arbitrary CLR type names, or one progress row per batch item.
- Throwing exceptions for expected validation, batch, checkpoint, fan-in, notification-projection, or known transient outcomes instead of returning and propagating the applicable `TaskResult`.
- Treating a SignalR connection as proof that no updates were missed.
- Reusing one browser store snapshot after the authenticated user or active division changes.
- Showing all division-scoped operations when the active-division header is absent or no longer authorized.
- Using `DeleteBehavior.SetNull` for the operation division relationship and thereby globalizing retained scoped history.
- Deleting unprojected milestone events merely to meet the ordinary event-retention target.
- Allowing a browser or enqueue call to choose an unregistered Hangfire queue.
- Retrying an operation after its retained payload has been redacted.

## Verification

Test successful processing, retry, duplicate delivery, publication failure, rollback, job deduplication, and notification state. For notification dispatchers, block one attempt deliberately and verify queued deliveries remain queued until worker capacity exists, attempts are recorded before dispatch, configured concurrency removes head-of-line blocking, and stale results cannot overwrite newer attempts. Run claim and transaction-lock tests on SQL Server and PostgreSQL, and verify scoped DI validation at host startup.

For background operations, also test enqueue idempotency, conflict behavior, global/queue/type/resource concurrency, mixed single/multi-resource acquisition, lease expiry and monotonically increasing fencing tokens, required-lease and transaction-lock rescheduling, stale-attempt reconciliation, cancellation before and during execution, retry exhaustion, checkpoint compare-and-swap, weighted nested progress, batch counters, fan-out idempotency, parent suspension without worker starvation, child progress aggregation, a contended final-child wake-up through dispatcher redispatch into the next parent step, fan-in failure policy, hierarchy cancellation/retry, leaf-first retention, event ordering and milestone protection, notification projection retry, reconnect resync, polling recovery, user/division scope changes with late responses, retention/redaction, readiness health, and authorization isolation. Exercise persistence and translated queries on both SQL Server and PostgreSQL; EF Core InMemory is not relational evidence.
