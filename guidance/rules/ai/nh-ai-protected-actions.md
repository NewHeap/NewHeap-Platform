---
id: nh-ai-protected-actions
title: "Protect AI mutations with exact approval and verification"
area: backend
reference: ai-protected-actions
summary: "Route material AI actions through scoped capabilities, canonical proposals, bound approvals, budgets, concurrency, idempotency, and independent verification before reporting success."
sample-cases: ["SPM-224", "SPM-240", "SPM-268", "SPM-356", "SPM-372"]
public-symbols: ["INhAiApprovalEvidenceProvider", "INhAiApprovalValidator", "INhAiAuthoritativeExecutionEvidenceValidator", "INhAiBudgetManager", "INhAiCapabilityResolver", "INhAiEffectPolicy", "INhAiIdempotencyManager", "INhAiProposalFactory", "INhAiToolConcurrencyLimiter", "INhAiToolVerifier", "NhAiActionBudget", "NhAiApproval", "NhAiApprovalEvidence", "NhAiApprovalRequirement", "NhAiApprovalRole", "NhAiAuditRecord", "NhAiAuthoritativeExecutionEvidence", "NhAiCapabilityGrant", "NhAiCapabilityResolution", "NhAiEffectDecisionKind", "NhAiIdempotencyRequest", "NhAiIdempotencySupport", "NhAiInProcessConcurrencyOptions", "NhAiProposal", "NhAiProposalCreateRequest", "NhAiToolConcurrencyPartition", "NhAiToolEffect", "NhAiToolFailureCodes", "NhAiVerificationResult", "AllowAgentGrant", "NhAiGrantSource", "NhAiToolCallOrigin", "NhAiToolCallOriginKind", "INhAssistantApprovalPolicySelector", "NhAssistantApprovalPolicySelection"]
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
time window. An agent cannot approve its own proposal. The shared invoker
validates that time window at the application's clock (a registered
`TimeProvider`, otherwise the system clock), so take the proposal's `GeneratedAt`
and `ExpiresAt` and the approval's `ApprovedAt` and `ExpiresAt` from the same
clock.

When approval and idempotency are already authoritative in the consuming domain,
register one `INhAiAuthoritativeExecutionEvidenceValidator`. Validate the
consumer-owned evidence there and return only the bounded normalized attestation;
do not translate it into a second Platform proposal. The shared invoker still
performs authorization, capability, budget, concurrency, idempotency lease,
execution, verification, and audit handling. When an expected denial must reach
the caller as the domain's own typed receipt, return a failed `TaskResult` whose
data is `NhAiAuthoritativeExecutionEvidence.Denied(payload, evidenceReference)`;
the invoker copies the payload into the failed `TaskResult<T>.Data` and audits
the failure code as the approval code.

Every call the invoker ends without success records the code its caller receives
in the shared tool result as the audit record's `ResultCode`, next to the
`Outcome` kind:

- `AuthorizationDenied`: the invocation gate's own code (`ai-tool-authorization-denied`
  when the gate gives none), `ai-tool-capability-denied`, the authoritative denial's
  own code (`ai-tool-execution-evidence-invalid` when it has none, and for evidence
  of an invalid shape, which is audited without its reference or key),
  `ai-tool-effect-denied` and `ai-tool-approval-delegation-invalid`;
- `ApprovalRequired`: `ai-tool-approval-required` for a missing approval, without an
  `ApprovalCode`, and `ai-tool-approval-invalid` for a rejected one, with the
  validator's code as `ApprovalCode`;
- `BudgetExhausted`: `ai-tool-budget-exhausted` for an invalid remaining budget,
  `ai-tool-budget-denied` for a refused reservation and `ai-tool-deadline-expired`;
- `Conflict`: `ai-tool-concurrency-limited`, `ai-tool-idempotency-key-invalid` and
  `ai-tool-idempotency-denied`, with the lease's own code as `IdempotencyCode`;
- `TerminalFailure`: `ai-tool-input-too-large`, `ai-tool-result-too-large`,
  `ai-tool-verification-failed` with the verifier's `VerificationCode`, the first
  code of the tool's failed result (`ai-tool-failed` without one), and
  `ai-tool-failed` for an exception, which still propagates.

A success has no `ResultCode`. Read the reason of a refusal from `ResultCode`, its
kind from `Outcome` and the detail of one step from `ApprovalCode`,
`IdempotencyCode` and `VerificationCode`; the record never carries arguments,
results or exception messages.

When the application must validate approval and reconcile replays inside the
write itself—for example an engine that returns a receipt with a stable denial
code, burns the grant on every mismatch, and answers a replayed idempotency key
with the reconciled receipt—declare `Approval = NhAiApprovalRequirement.ConsumerAuthoritative`
and `Idempotency = NhAiIdempotencySupport.ConsumerAuthoritative` on that tool.
The descriptor then states honestly that the consumer owns those steps: the
default effect policy returns `NhAiEffectDecisionKind.ConsumerAuthoritativeApproval`,
the invoker skips the Platform approval evidence and idempotency lease for that
tool only, and the audit record carries `ApprovalCode` and `IdempotencyCode`
`consumer-authoritative` plus the `ResultCode` of a failed invocation. Return an
expected denial as `TaskResult<T>.Failed(code, message).WithData(receipt)`.
Tools that do not opt in keep Platform approval and leases, an effect policy
cannot delegate approval to a tool that did not opt in, and destructive effects
always require Platform approval and a verifier. Do not use these values to
weaken a tool that has no authoritative engine of its own; use
`NotRequired`, an attesting `ApprovalValidated = true`, or an always-acquiring
lease manager only when that is literally true.

When a tool issues the approval artifact that another tool consumes, such as a
single-use grant created under the application's own write authorization, declare
`Approval = NhAiApprovalRequirement.Issuer` on the issuing tool. An issuer is a
non-read, non-destructive side effect without a Platform idempotency lease; the
generator rejects an issuer with `Idempotency = Required` or `Supported`, a
read-only issuer, and a destructive issuer with `NHAI008`. The invoker still
authorizes, resolves capabilities, reserves budget, bounds execution and audits
it, and records the approval code `issuer`. `NhAiToolDescriptor.ApprovalRole`
exposes `Issuer`, `Consumer` or `None`, so discovery policies and reviewers can
tell an issuing tool apart from a consuming write bound to the same grant.

When the consuming write validates its grant in the
`INhAiAuthoritativeExecutionEvidenceValidator`, burn the grant there and return a
denial with `NhAiAuthoritativeExecutionEvidence.Denied(receipt, evidenceReference)`.
Let a replayed idempotency key pass the validator untouched so the engine can
return the reconciled receipt. The failure code, message and evidence reference
then reach the audit record and MCP callers.

An agent that runs on its own may use a tool beyond the rights of the person it
works for only when two keys agree: the tool declares
`AllowAgentGrant = true`, which requires agent exposure and an `[Authorize]`
policy (the generator reports `NHAI014` otherwise), and the agent registration
grants the tool. Declare `ApprovalPolicy` on the tool when approvers must satisfy
a different policy than callers. An agent that answers for itself never
executes a non-read effect without approval: the default effect policy turns
every such call into a proposal and denies issuer tools. The audit record names
the grant source (`NhAiGrantSource.Delegated` or `Agent`), the agent, the
accountable owner and the proposal, so reviewers can tell elevated calls from
calls within a person's own rights. The contract hash of a tool changes when it
opts in, so existing approvals for that tool expire.

Who approves a proposal of the assistant is a named approval policy that code resolves. When it depends
on the proposal itself, such as four eyes only while a second person may approve, choose among registered
policies with an `INhAssistantApprovalPolicySelector` (`UseApprovalPolicySelector`, SPM-356) and count the
people a policy admits with `INhAssistantApproverDirectory` (SPM-357); never relax the policy at the
decision. The proposal keeps the chosen policy in its copy, so the decision checks what was chosen, and a
selector that chooses an unknown policy, throws or misses its time limit refuses the proposal. See
`nh-ai-agent-runs` for the selection and the directory.

Resolve short-lived capabilities again at discovery and invocation. Bind grants
to subject, purpose, tool selector, execution scope, issuer, expiry, optional
budget, and revocation evidence. Always reserve a declared budget through
`INhAiBudgetManager` before acquiring an
idempotency lease. Use a durable application implementation of
`INhAiIdempotencyManager` for production side effects and carry fencing tokens
across retry boundaries. The in-memory sample manager is executable evidence,
not durable production storage.

The default `INhAiToolConcurrencyLimiter` admits at most `MaxConcurrency`
concurrent invocations per tool ID and version, per process and per
`NhAiInvocationContext.TenantId`. A tenant that fills a tool's bound never
refuses another tenant's call; invocations without a tenant share one partition.
A refusal is the expected `ai-tool-concurrency-limited` result, not an exception.
State the partition with `UseInProcessConcurrencyLimiter` and select
`NhAiToolConcurrencyPartition.Tool` only when the bound protects a resource that
every tenant shares. Register a distributed limiter through
`UseConcurrencyLimiter<TLimiter>()` when several instances must share a bound.

Run the configured verifier after a successful application-service result. The
verifier should re-read the actual resource or remote system within the same
authorized scope. If verification disagrees, preserve the execution report as
evidence but return a failed `TaskResult`; do not turn acceptance by a dependency
into verified success. Audit only bounded IDs, classifications, outcome codes,
and evidence references—never arguments, results, prompts, or credentials.

A call that a composition or a script makes is still a call of its own tool and
gets exactly that tool's decision: rights, effect, approval, guards, budget and
audit. The invoker marks it with `NhAiInvocationContext.Origin`, an
`NhAiToolCallOrigin` with its `NhAiToolCallOriginKind` (`Composition` or
`Script`), the composition's or script tool's id, the script's hash and the
invocation of the call that ran it with the step's position, and writes the same
on `NhAiAuditRecord.Origin`, so each step is audited under that call. A proposal
a step makes shows it on `NhAiProposalGuardContext.Origin` at the proposal stage
(SPM-372). A tool or guard that must treat such a call differently, for example a
change it allows only as a call the model chose itself, refuses or checks it on
`Origin`; never relax a rule for it. The origin takes part in no proposal hash or
approval evidence, and a call the tool makes internally carries none.

## Avoid

- Adding `AllowAgentGrant` to a tool merely so that an agent can call it, without a policy that approvers are checked against.

- Treating a conversational confirmation as approval.
- Letting an agent approve its own proposal or widening the proposal after approval.
- Retrying a side effect without a stable idempotency key and fencing contract.
- Trusting the tool response as independent verification.
- Using the in-process concurrency limiter or sample memory store as distributed durability.
- Sharing one tool concurrency bound across tenants when it does not protect a resource that every
  tenant shares; one tenant's load would then refuse every other tenant's calls.
- Logging proposal arguments, execution results, provider bodies, or retrieved content.
- Declaring `NotRequired` approval, attesting `ApprovalValidated = true` before validation, or
  registering an always-acquiring lease manager to bypass governance for a tool whose
  application does not actually own approval or replay reconciliation.
- Declaring `ConsumerAuthoritative` approval or idempotency on a tool that does not validate
  its own grant and reconcile replays inside the invocation.
- Declaring a grant-issuing tool as a consuming write, or a consuming write as an issuer.
- Declaring the destructive effect only to publish a destructive MCP hint; use a narrowing
  hint override so approval semantics stay honest.

## Verification

Exercise changed arguments, targets, constraints, budgets, expiry, agent
self-approval, revoked or expired capabilities, omitted/denied budget reservation,
concurrency saturation within one tenant while another tenant stays admitted,
duplicate and conflicting idempotency keys, fencing, verifier disagreement,
timeout, and bounded results. Prove the application service executes once across
a retry and that a verification failure remains distinguishable from an
execution failure. For a consumer-authoritative tool,
prove that an invalid or burned grant returns the typed denial receipt as failed
`TaskResult<T>.Data` without executing, that a replayed key reaches the engine
and returns the reconciled receipt while the Platform idempotency manager is
never asked for a lease, that the audit record carries the consumer-attested
codes, and that a delegating effect decision is rejected for a tool that did not
opt in. Issue a grant through an issuer tool and consume it through the consuming
write; assert the audit codes `issuer` and the validated or denied consumer code,
and assert the generator rejects an issuer that declares a Platform idempotency
lease. For every refusal, assert that the audit record's `ResultCode` equals the
`code` of the shared tool result (`NhAiToolResult.CodePropertyName`), as the
library's own tests do for each exit of the invoker. SPM-224, SPM-240 and `NewHeap.Platform.AI.Tests` are the executable
references.
