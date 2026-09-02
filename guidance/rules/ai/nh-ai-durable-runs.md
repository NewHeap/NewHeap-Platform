---
id: nh-ai-durable-runs
title: "Run approval-gated AI work inside durable background operations"
area: backend
reference: ai-durable-runs
summary: "Use background operations as the outer durability boundary and persist application state plus versioned checkpoint references instead of treating chat history as authoritative state."
sample-cases: ["SPM-230"]
public-symbols: ["INhAiBackgroundOperationRunAdapter", "NhAiRunCheckpointReference", "NhAiRunCheckpointReferenceFactory", "NhAiBackgroundApprovalSignal"]
skills: ["newheap-background-processing", "newheap-backend-development"]
providers: ["provider-neutral"]
risk: high
---
## Preferred approach

Use direct request-bound model calls for short work. For work that can outlive a
request, inject `INhAiBackgroundOperationRunAdapter` into a registered background
operation handler. Bind a non-human `NhAiInvocationContext` with an accountable
owner. The adapter maps the operation ID, attempt number, idempotency key,
fencing token and shortest deadline into the AI run.

Persist authoritative application inputs and generated artifact references in
application checkpoints or storage. Persist only a
`NhAiRunCheckpointReference` for provider or workflow state, including stable
adapter/workflow versions, checkpoint schema and a content hash. A re-entered
handler reconstructs work from these records; conversation history is optional
context and never the durable state machine.

For material output, create the exact proposal before suspension and use the
general signal contract for its application-authorized approval. The server,
not the enqueue caller, creates and checkpoints the canonical proposal with its
ID/hash, actor, owner, run, exact arguments, targets, constraints, budget,
versions, and expiry. After wake-up, build approval evidence from the immutable
checkpoint plus the durable approver identity/time and validate it with
`INhAiApprovalValidator`; never accept a client-originated proposal hash as the
authority. Protect publication or other side effects
with background-operation idempotency and fencing, then expose only an
application-owned result reference.

## Avoid

- Keeping a request, model stream or worker alive while awaiting approval.
- Serializing full chat history, prompts, credentials or model internals as a workflow checkpoint.
- Repeating a side effect because the handler starts from the beginning after wake-up.
- Using Agent Framework checkpoint internals as the general application contract.
- Allowing a background operation to weaken tool approval, capability or verification policy.
- Trusting a proposal ID/hash supplied by the client instead of the server checkpoint.

## Verification

Interrupt after context capture, suspend for approval, wake with a matching
signal and re-enter the handler. Assert the run ID and attempt mapping, stable
snapshot/hash, versioned checkpoint reference, exact proposal binding,
idempotent publication, fencing behavior, cancellation, progress milestones and
absence of chat history from authoritative recovery. SPM-230 is the executable
reference.
