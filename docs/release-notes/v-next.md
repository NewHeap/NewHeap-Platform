# v-next

## NewHeap.Platform.DatabaseRead.Tool: an untrusted TLS certificate, schema search on PostgreSQL and the verification check

A server certificate that the diagnostic host does not trust no longer reads as a transient network
failure. SQL Server `SEC_E_UNTRUSTED_ROOT` (`-2146893019`), `CERT_E_UNTRUSTEDROOT` and `CERT_E_CHAINING`,
and a certificate that .NET certificate validation rejects on either provider, return the new
classification `tls-certificate-untrusted`: not transient, with the new retry hint
`certificate-authority-trust-required`, stage `connection-open` and, on SQL Server, the SSPI code as
`providerCode`. The response still never contains the provider message. Npgsql does not say which check
failed, so on PostgreSQL a certificate rejected for its host name or validity period reports the same
classification; on SQL Server those codes remain `connection-failed`.

A PostgreSQL schema search without a schema, a search term or both failed with SQLSTATE `42P08`; the
omitted values are now bound as text. Permission verification, visibility and limits are unchanged.

Since 7.1.0, a rejected SQL Server principal reports the first rejected check as `error.verificationCheck`
next to `read-only-principal-not-verified`, on the CLI and in MCP results; this note was missing.
The function policy stays strict: SELECT or EXECUTE on any user-defined function rejects the principal
with `function-select` or `function-execute`.

| Breaking change | Required action |
|---|---|
| An untrusted server certificate returns classification `tls-certificate-untrusted`, not transient, with retry hint `certificate-authority-trust-required`, instead of the transient `connection-failed` with `network-access-required`. | Let code that maps classifications or retry hints handle values it does not know, do not retry this one, and trust the server's certificate authority on the diagnostic host. |

## NewHeap.Platform.AI.Common and NewHeap.Platform.AI.Chat: the code of every refused tool call in the audit

The invoker now records the code its caller receives as `NhAiAuditRecord.ResultCode` for every call it
ends without success, not only for the four refusals of v20.0.0. This note supersedes the v20.0.0 note
"the real code of a refused tool call", which named only the gate, capability, effect and
approval-delegation refusals. The complete set, by `Outcome`:

- `AuthorizationDenied`: the invocation gate's own code (`ai-tool-authorization-denied` when the gate
  gives none), `ai-tool-capability-denied`, the authoritative denial's own code
  (`ai-tool-execution-evidence-invalid` when it has none, and for evidence of an invalid shape),
  `ai-tool-effect-denied` and `ai-tool-approval-delegation-invalid`;
- `ApprovalRequired`: `ai-tool-approval-required` (without an `ApprovalCode`) and
  `ai-tool-approval-invalid` (with the validator's code as `ApprovalCode`);
- `BudgetExhausted`: `ai-tool-budget-exhausted`, `ai-tool-budget-denied` and `ai-tool-deadline-expired`;
- `Conflict`: `ai-tool-concurrency-limited`, `ai-tool-idempotency-key-invalid` and
  `ai-tool-idempotency-denied` (with the lease's code as `IdempotencyCode`);
- `TerminalFailure`: `ai-tool-input-too-large`, `ai-tool-result-too-large`, `ai-tool-verification-failed`
  (with the verifier's `VerificationCode`), the first code of the tool's failed result (`ai-tool-failed`
  without one) and `ai-tool-failed` for an exception, which still propagates.

Authoritative execution evidence of an invalid shape, which the invoker refused without an audit, is now
audited as `AuthorizationDenied` with `ApprovalCode` `execution-evidence-invalid`, without its evidence
reference or idempotency key. A success keeps `ResultCode` `null`, and the record stays content-free.
The assistant uses `ResultCode` before the outcome kind, so its tool calls, `ToolInvoked` audit events,
the model's summary of a call and proposal execution results name these codes too.

| Breaking change | Required action |
|---|---|
| `NhAiAuditRecord.ResultCode` is set for every refusal and failure of the invoker listed above instead of `null`; a failed result or a gate refusal or authoritative denial without a code of its own records `ai-tool-failed`, `ai-tool-authorization-denied` or `ai-tool-execution-evidence-invalid` instead of `null`. | Read the reason of a failed call from `ResultCode` and its kind from `Outcome`; stop treating a `null` `ResultCode` on a failure as "no code". |
| Authoritative execution evidence of an invalid shape now writes an `AuthorizationDenied` audit record, and in the assistant a `ToolInvoked` event, where it wrote none. | Let audit sinks and reports that count refusals expect one record more for such a call. |
| Assistant tool calls, `ToolInvoked` events and proposal execution results carry `ai-tool-budget-exhausted` (invalid remaining budget) and `ai-tool-deadline-expired` instead of `ai-tool-budget-denied`, `ai-tool-idempotency-key-invalid` instead of `idempotency-key-invalid`, `ai-tool-idempotency-denied` instead of the lease's code (such as `duplicate`, `idempotency-conflict` or `idempotency-in-progress`), and `ai-tool-input-too-large`, `ai-tool-result-too-large` and `ai-tool-execution-evidence-invalid` (invalid evidence shape) instead of `ai-tool-failed`. | Update filters, reports and translations that match the old codes; read the lease's own code from the audit record's `IdempotencyCode`. |

## NewHeap.Platform.AI.Common, NewHeap.Platform.AI.AspNet.Common and NewHeap.Platform.AI.Chat: the agent run id on the invocation context

`NhAiInvocationContext.AgentRunId` and `NhAiAgentInvocationScope.AgentRunId` (`Guid?`) name the agent run
a tool call belongs to. The assistant sets it on the agent invocation scope and the agent's own
invocation context of a run, and the agent invocation gate and the agent context factory copy it to
every tool call of the run, also when an approved proposal of the run executes. It is `null` outside a
run: in a person's chat, a person's turn in the agent's thread and a consultation. `RunId` stays the
turn's id and `CorrelationId` the run id in `N` format, because proposal hashes and usage records depend
on them.

| Breaking change | Required action |
|---|---|
| None; `AgentRunId` is additive on both records. | None. A tool that took the run's id from `CorrelationId` reads `AgentRunId` instead; a host that builds `NhAiAgentInvocationScope` itself sets `AgentRunId` for a run. |

## NewHeap.Platform.AI.Common: the media type check without a match timeout

`NhAiMediaTypes.Normalize` and `IsValid`, and so `NhAiFileReference.IsValid`, check media types with a
generated regular expression without a match timeout. The pattern is linear and the value at most 128
characters, so a busy machine no longer turns a valid media type into a `RegexMatchTimeoutException`
during startup or file handling. Which values are valid does not change.

| Breaking change | Required action |
|---|---|
| None; the media type check no longer throws `RegexMatchTimeoutException`. | None. |
