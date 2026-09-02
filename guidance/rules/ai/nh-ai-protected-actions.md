---
id: nh-ai-protected-actions
title: "Protect AI mutations with exact approval and verification"
area: backend
reference: ai-protected-actions
summary: "Route material AI actions through scoped capabilities, canonical proposals, bound approvals, budgets, concurrency, idempotency, and independent verification before reporting success."
sample-cases: ["SPM-224"]
public-symbols: ["INhAiApprovalEvidenceProvider", "INhAiApprovalValidator", "INhAiBudgetManager", "INhAiCapabilityResolver", "INhAiEffectPolicy", "INhAiIdempotencyManager", "INhAiProposalFactory", "INhAiToolConcurrencyLimiter", "INhAiToolVerifier", "NhAiActionBudget", "NhAiApproval", "NhAiApprovalEvidence", "NhAiCapabilityGrant", "NhAiCapabilityResolution", "NhAiIdempotencyRequest", "NhAiProposal", "NhAiProposalCreateRequest", "NhAiToolEffect", "NhAiVerificationResult"]
skills: ["newheap-backend-development"]
providers: ["provider-neutral"]
risk: high
---
## Preferred approach

Classify every generated tool effect explicitly. Keep reads read-only and require
stable idempotency for every non-read effect. Require bound approval for generic
mutations, external side effects, and destructive actions; register an
independent verifier for destructive or otherwise high-impact actions. The
analyzer rejects incomplete high-risk descriptors and startup validation rejects
missing budget/idempotency managers or verifiers.

Create `NhAiProposal` through `INhAiProposalFactory`. Include the exact typed
arguments, targets, constraints, action budget, actor, accountable owner, tool
contract hash, intent, expected effects, and expiry. Persist approval evidence in
the consuming application. An `NhAiApproval` is valid only for that canonical
proposal hash, approving actor, target set, constraints, maximum budget, and
time window. An agent cannot approve its own proposal.

Resolve short-lived capabilities again at discovery and invocation. Bind grants
to subject, purpose, tool selector, execution scope, issuer, expiry, optional
budget, and revocation evidence. Always reserve a declared budget through
`INhAiBudgetManager` before acquiring an
idempotency lease. Use a durable application implementation of
`INhAiIdempotencyManager` for production side effects and carry fencing tokens
across retry boundaries. The in-memory sample manager is executable evidence,
not durable production storage.

Run the configured verifier after a successful application-service result. The
verifier should re-read the actual resource or remote system within the same
authorized scope. If verification disagrees, preserve the execution report as
evidence but return a failed `TaskResult`; do not turn acceptance by a dependency
into verified success. Audit only bounded IDs, classifications, outcome codes,
and evidence references—never arguments, results, prompts, or credentials.

## Avoid

- Treating a conversational confirmation as approval.
- Letting an agent approve its own proposal or widening the proposal after approval.
- Retrying a side effect without a stable idempotency key and fencing contract.
- Trusting the tool response as independent verification.
- Using the in-process concurrency limiter or sample memory store as distributed durability.
- Logging proposal arguments, execution results, provider bodies, or retrieved content.

## Verification

Exercise changed arguments, targets, constraints, budgets, expiry, agent
self-approval, revoked or expired capabilities, omitted/denied budget reservation,
concurrency saturation, duplicate and conflicting idempotency keys, fencing,
verifier disagreement, timeout, and bounded results. Prove the application
service executes once across a retry and that a verification failure remains
distinguishable from an execution failure. SPM-224 and
`NewHeap.Platform.AI.Tests` are the executable references.
