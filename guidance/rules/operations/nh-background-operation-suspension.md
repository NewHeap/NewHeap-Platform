---
id: nh-background-operation-suspension
title: "Suspend a background operation for authorized external input"
area: backend
reference: background-operation-suspension
summary: "Release the worker and persist a typed, expiring wait contract when a durable operation needs approval or other authorized external input."
sample-cases: ["SPM-229", "SPM-230"]
public-symbols: ["INhBackgroundOperationSuspensionContext", "INhBackgroundOperationSignalService", "NhBackgroundOperationSignalWaitResult", "NhBackgroundOperationSignalWriteResult"]
skills: ["newheap-background-processing"]
providers: ["sql-server", "postgresql"]
risk: high
---
## Preferred approach

Call `context.Suspension.WaitForSignalAsync<TSignal>` with a stable dash-case
wait key, explicit signal schema version and bounded expiry. The first call
atomically writes the typed wait checkpoint, closes the current attempt as
`Suspended`, moves the operation to `WaitingForSignal`, and releases its worker
and leases. The handler is re-entered from the beginning after wake-up, so
protect all earlier material work with application checkpoints and
`context.Idempotency`.

After application authorization succeeds, use
`INhBackgroundOperationSignalService.SignalForOwnerAsync`. Pass the persisted
operation owner and the independently authenticated signaling actor. The
persistence boundary matches the owner, wait key and schema, rejects expired or
conflicting signals, treats an identical duplicate idempotently, and makes the
operation dispatchable. Never put authorization claims inside the signal body.

Expiry is stored as `NextDispatchAt`; it wakes a new attempt that receives an
`Expired` result. Cancellation advances a sleeping operation immediately for
terminal handling. No worker polls and no public control-flow exception leaks
into handlers.

## Avoid

- Sleeping, polling or repeatedly retrying while waiting for a human.
- Treating a browser-selected division or the signal payload as authorization evidence.
- Performing uncheckpointed work before a wait.
- Changing the wait key, schema version or expiry after the operation suspended.
- Accepting a second materially different signal.
- Storing credentials, prompts or large artifacts in the signal checkpoint.

## Verification

Run the same relational scenario on SQL Server and PostgreSQL. Assert the first
attempt becomes suspended with no current owner, no worker remains active, an
identical signal is a duplicate, a different signal conflicts, the dispatcher
creates a new fenced attempt, completed idempotent work does not repeat, and the
operation still supports cancellation and expiry. SPM-229 is the executable
reference; SPM-230 shows the AI approval use case.
