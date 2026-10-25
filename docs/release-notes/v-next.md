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

## NewHeap.Platform.AI.Common, NewHeap.Platform.AI.AgentFramework and NewHeap.Platform.AI.Chat: a model timeout is a failed result

A governed deadline no longer throws. When the profile's timeout or the invocation context's
`Deadline` ends a model call before it answers, after any retry the profile allows,
`INhAiChatExecutor.GetResponseAsync` returns the failed result `ai-model-timeout`
(`NhAiModelFailureCodes.Timeout`). A stream ends its enumeration without an exception, keeps the
updates it delivered, and completes with that failed result. A cancellation the caller did not
request, such as the provider HTTP client's own timeout, counts the same. The usage record and the
`ai.model.invoke` activity carry the new outcome `NhAiOutcomeKind.TimedOut` and finish reason
`timed-out`. The caller's own cancellation still throws `OperationCanceledException` and records
`TerminalFailure` with `cancelled`; both keep one budget reservation and one usage record per call.
`NhAiModelFailureCodes` and `NhAiModelFinishReasons` name the executor's codes and finish reasons.

The Agent Framework adapter's governed chat client, which can only answer or throw, throws the new
`NhAiAgentModelCallException` for a failed call, a stream that could not start and a failed stream
completion. It is an `InvalidOperationException` with the unchanged message, and `Code` keeps the
result's failure code. The assistant maps `ai-model-timeout` to the codes a timeout gave before: a
turn ends with `assistant-turn-timeout` and a consultation with `assistant-consult-timeout`; a
triage that times out stays undecided.

| Breaking change | Required action |
|---|---|
| `INhAiChatExecutor.GetResponseAsync` returns the failed result `ai-model-timeout` when a profile timeout, the invocation deadline or a cancellation the caller did not request ends the call, instead of throwing `OperationCanceledException`; a stream ends its enumeration and fails `Completion` with `ai-model-timeout` instead of throwing and cancelling `Completion`. | Handle `NhAiModelFailureCodes.Timeout` as a failed result where code caught `OperationCanceledException` for a timeout; keep catching it only for the caller's own cancellation, and await `Completion` after every stream. |
| The usage record and `ai.model.invoke` activity of such a call have outcome `NhAiOutcomeKind.TimedOut` (9) and finish reason `timed-out` instead of `TerminalFailure` and `cancelled`. | Let usage sinks, reports and dashboards that map `NhAiOutcomeKind` or finish reasons handle `TimedOut` and `timed-out`. |
| The Agent Framework adapter's chat client throws `NhAiAgentModelCallException` instead of `InvalidOperationException` for a failed governed call or stream, and so also for a timeout, which threw `OperationCanceledException` before. | Catch `NhAiAgentModelCallException` and read `Code`, such as `ai-model-timeout`; code that catches `InvalidOperationException` keeps working, but replace assertions of that exact type. |

## NewHeap.Platform.AI.AgentFramework: chat history and AI context providers for agents

`NhAiAgentCreateRequest` takes two optional factories that the adapter calls once per created agent
with the service provider passed to `CreateAsync`: `ChatHistoryProviderFactory` creates the
agent's Agent Framework `ChatHistoryProvider`, such as an `InMemoryChatHistoryProvider` or one over
the application's own store, and `AIContextProvidersFactory` its `AIContextProvider`s, such as memory
or compaction. Both go into the `ChatClientAgentOptions` the adapter builds. The governed client, the
published tools and the invocation context stay NewHeap's: every model call still crosses
`INhAiChatExecutor`, and a context provider that adds a tool fails the run with
`InvalidOperationException` before the model is called.

| Breaking change | Required action |
|---|---|
| None; both factories are optional, and an agent without them keeps Agent Framework's defaults. | None. Publish tools through a governed tool catalog, not through an AI context provider. |

## NewHeap.Platform.AI.Test: strict structured output in the test clients

`NhAiDeterministicChatClient.StrictStructuredOutput` and `NhAiScriptedChatClient.StrictStructuredOutput`,
off by default, check the JSON schema of `ChatOptions.ResponseFormat` as a provider in strict mode does
and refuse the request with an `HttpRequestException` of status 400 when a `$ref` names anything but the
root or an entry of the root's `$defs`, an object does not set `additionalProperties` to `false`, or an
object leaves a property out of `required`. `NhAiStrictStructuredOutput.Validate` returns every
`NhAiStrictSchemaViolation` with its JSON pointer. NewHeap still passes schemas on unchanged; the
guidance shows what Microsoft.Extensions.AI generates for repeated types, referenced arrays, nullable
objects and recursive definitions, and the application normalizes its own schemas.

| Breaking change | Required action |
|---|---|
| None; the check is opt-in. | None. Set `StrictStructuredOutput` in tests whose calls send a structured-output schema. |

## NewHeap.Platform.AspNet.Common: background-operation failures and cancellations

Three fixes keep a background operation's outcome recordable and true to what stopped it.

- The runner checks a failed or retry result's failure code and message key against their columns
  (100 and 300 characters) before it writes them. A code that is too long is stored as
  `handler-rejected` and a key as the generic `background-operation.failed`; the returned text goes
  to the operational log only. The completion no longer fails on the database (PostgreSQL 22001),
  so the attempt always completes, an expected failure stays terminal and a retry result keeps its
  retry.
- A progress step whose action threw keeps its own exception, a cancellation included, when the
  write that records the step as failed or cancelled fails; that failure is logged as a warning with
  the operation and attempt. A lost fence, lock contention and a durable cancellation request that
  the write finds after the step's own error still stop the attempt.
- A cancellation the heartbeat did not record is a time-out only when the soft time-out fired.
  Otherwise the runner checks the attempt's durable cancellation and ends the operation `Cancelled`
  when one was requested; any other cancellation is reported as a time-out, as before.

| Breaking change | Required action |
|---|---|
| A handler that stops on a durable cancellation before the heartbeat sees it, through `ThrowIfCancellationRequestedAsync` or a write of the attempt, ends the operation `Cancelled` (`cancelled`, `background-operation.cancelled`) instead of `TimedOut` (`soft-timeout`), and the default notification policy no longer reports a time-out for a cancellation the owner requested. | None, unless monitoring or a notification policy counted these cancellations as time-outs; expect `Cancelled`. |
| A failure code of 101 to 200 characters is stored as `handler-rejected`, and a message key longer than 300 characters as `background-operation.failed`, instead of failing the completion write. | Return failure codes of at most 100 characters and translation keys of at most 300; read the returned text from the operational log. |
| When the write of a failed or cancelled step fails, the handler and the runner receive the step's own exception instead of the database exception. | None, unless a handler catches the database exception of that write; catch the step's own exception instead. |
| A write of an attempt that lost its fence throws an internal subclass of `InvalidOperationException` with the same message. | Assert it in tests with a check that accepts derived types, such as `Assert.ThrowsAnyAsync<InvalidOperationException>`. |

## NewHeap.Platform.AspNet.Common: an attempt's own write between slow heartbeats

A heartbeat that takes longer than `HeartbeatInterval`, as on a busy database, has its next beat due at
once. An own write of the attempt that waited for a beat (a message, step, progress, checkpoint, signal
suspension, fan-out or the completion) could then wait behind every following beat, so the attempt kept
sending heartbeats but never completed or suspended. The end of a beat now admits the writes that waited
for it before the next beat can start; that beat skips or waits for them, as for any own write in
progress, so a write waits for at most one beat. The heartbeat still never waits for the operation lock
behind an own write, and an own write that never ends still lets the heartbeat go stale.

| Breaking change | Required action |
|---|---|
| None; an attempt whose heartbeats are slower than their interval now completes or suspends instead of hanging. | None. |

## NewHeap.Platform.AspNet.Common and NewHeap.Platform.AI.Chat.AspNet: reserved capacity for agent runs

`NhBackgroundOperationBuilder.WithReservedQueueConcurrency(queue, slots)` reserves slots of the global
limit (`WithGlobalConcurrency`) for one queue, in `NhBackgroundOperationsOptions.ReservedQueueConcurrency`.
Operations on that queue take a reserved slot first and a free shared slot when their reserved ones are
in use; operations on every other queue share only the slots the reservations leave, so they never take a
reserved slot, and the slots in use never exceed the global limit. Host start fails without a global
limit, when the reservations leave no shared slot, and when a queue reserves more slots than its
`WithQueueConcurrency` limit. Without reservations the global lease is unchanged.

`AddAssistantAgentRuns(runs => runs.Queue("agent-runs").MaxConcurrency(4))` places every operation of the
assistant (runs, proposal executions, revalidations, deferred trigger fires and replays) on one queue,
which the process's Hangfire server serves on its own, and gives that queue its concurrency limit.
`MaxConcurrency` needs a queue of their own and refuses to change a limit the queue already has.
`AddAssistantAgentRuns()` without a choice keeps them on the default queue.

| Breaking change | Required action |
|---|---|
| None; both are additive, and the assistant's operations stay on the default queue unless `Queue` is chosen. | None. A host that moves them to a queue keeps serving the old queue until the operations queued before have ended, and gives every process of its `ProcessorKey` the same reservations. |

## NewHeap.Platform.AspNet.Common and NewHeap.Platform.Events.Cap: opening a transaction asynchronously

`NhTransactionFactoryAsync` is a cancellation-aware transaction factory next to `NhTransactionFactory`:
`(prev, dbContext, serviceProvider, cancellationToken) => Task<IDbContextTransaction>`, registered as a
singleton. `StartTransactionAsync`, and with it `StartOrGetTransactionScopeAsync`, runs the synchronous
factories first and then the asynchronous ones, each in registration order and each receiving the
transaction the one before returned; without a factory it calls `BeginTransactionAsync` as before. A
cancelled token now stops the call before any factory runs, and when a factory throws or is cancelled,
the transaction an earlier factory returned is disposed, so the context no longer keeps a transaction
that a later scope would join without owning it. `NhTransactionFactory` stays for compatibility.

`AddCap` registers an asynchronous factory instead of its synchronous one. It opens the transaction with
`BeginTransactionAsync` and the caller's token, or wraps the transaction an earlier factory returned, and
sets `CapTransactionScope.Current` only once that transaction exists, so a cancelled open leaves neither a
transaction nor a CAP transaction. `CapEFDbTransaction.DisposeAsync` disposes the EF transaction
asynchronously, and both disposals also dispose it after a SQL Server commit, where CAP had released it
without disposing it. The outbox still commits and rolls back with the data.

| Breaking change | Required action |
|---|---|
| `AddCap` registers `NhTransactionFactoryAsync` instead of `NhTransactionFactory`, so `GetServices<NhTransactionFactory>()` no longer returns CAP's factory. | Open transactions through `StartTransactionAsync` or `StartOrGetTransactionScopeAsync`; resolve `NhTransactionFactoryAsync` where code called CAP's factory itself. |
| An application's `NhTransactionFactory` runs before CAP's factory whatever the registration order: one registered after `AddCap` receives `null` instead of CAP's transaction, and CAP wraps the transaction it returns. | Let such a factory open the transaction when it receives `null` (`prev ?? dbContext.Database.BeginTransaction(...)`), or register it as an `NhTransactionFactoryAsync` after `AddCap` to receive CAP's transaction. |
| `StartTransactionAsync` with an already cancelled token throws `OperationCanceledException` also when only synchronous factories are registered, where they opened a transaction before. | Handle the cancellation, or pass a token that is not cancelled. |

## NewHeap.Platform.AI.Chat: fixed patterns without a match timeout

The tool selector (`NhAssistantAgentDefinition`), the upload extension (`NhAssistantUploadOptions.MapExtension`)
and the artifact type patterns are generated regular expressions without a match timeout. They are linear,
so a busy machine no longer turns a valid selector into a `RegexMatchTimeoutException` while the host
starts. Which values are valid does not change.

| Breaking change | Required action |
|---|---|
| None. | None. |

## NewHeap.Platform.AI.Chat: the first reservations of a day at the same time

A budget reservation that found no ledger for the actor and day, while another reservation created that
ledger before this one checked for it, was refused with `assistant-budget-exhausted` although the day's
budget had room; concurrent first calls of a day, such as parallel tool calls, could be refused that way.
Such a reservation now books against the new ledger, and only an increment against a ledger that already
existed decides that the budget is exhausted. Concurrent reservations still never exceed the limit.

| Breaking change | Required action |
|---|---|
| None; reservations that the budget allows are no longer refused when the day's ledger is created at the same time. | None. |

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: the execution region at start, at save and per turn

Every profile the assistant uses must permit its execution region (`UseExecutionRegion`, `local` by
default), itself or through its fallbacks: the chat profile (`UseChatProfile`), the agents' profiles and
the profiles of the skills and the triage of agents in code. Otherwise the host does not start, and the
error names the profile and the region. Saving a data skill, also through a package import, or an agent
created in the administration with a profile whose chain does not permit the region fails with the field
error `profile: region-not-permitted` (`NhAssistantFieldErrors.RegionNotPermitted`); an agent's stored or
default profile is accepted as before. A turn whose chain permits the region for no profile that takes its
data classification fails at once with the new `assistant-model-region-denied`
(`NhAssistantErrorCodes.ModelRegionDenied`, HTTP 400), failure class `policy`, without a model call; a run
is not retried.

| Breaking change | Required action |
|---|---|
| A host whose chat, agent, skill or triage profile in code permits the execution region neither itself nor through a fallback no longer starts. | Permit the region on the profile or a fallback (`PermitExecutionRegions`), or choose the region with `UseExecutionRegion`. |
| A turn refused for its region fails with `assistant-model-region-denied` (failure class `policy`, not retried) instead of `assistant-model-unavailable` (transient, retried). | Handle the new code where turn or run failures are mapped or translated; give the agent a profile that runs in the region. |
| Saving a data skill or an agent created in the administration with such a profile fails with `profile: region-not-permitted` instead of succeeding. | Choose a profile that permits the region; show the field error in an own editor. |

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: a warning for a dropped tool presentation

A display name or approval presentation of an `INhAssistantToolPresenter` that breaks a cap is still
dropped as a whole, and the approver reads the generic summary; a warning now names the presenter's type,
the tool, the culture and the broken rule (`NhAssistantPresentationRules`), never the text. The caps are
public in `NhAssistantPresentationLimits`: a display name of 160 characters, a summary of 512, at most
eight fields with labels of 80 and values of 256 characters, a notice of 512, 4,000 characters of stored
JSON, two seconds per presenter call, and at most eight UI cultures. Approvals are presented in every
supported UI culture of the request localization, which `NewHeap:PlatformAspNetCommon:Settings:SupportedCultures`
fills; more cultures than the cap log one warning.

| Breaking change | Required action |
|---|---|
| None; the caps, including eight fields, are unchanged. | Keep a presenter within `NhAssistantPresentationLimits`; fix the presenter a warning names. |

## NewHeap.Platform.AI.Chat: shadow proposals follow the duplicate rules

A shadow proposal never waits for a decision, so every shadow run proposed an unchanged finding again. A
shadow proposal with the identity (tool and canonical arguments) of one recorded in the agent's shadow
threads of the scope within its proposal lifetime is no longer recorded: it is reported as `AlreadyOpen`
(`assistant-proposal-already-open`), as a live proposal would be, with the `AgentProposalDeduplicated`
audit event. The evaluation of a shadow variant therefore compares what the live agent would really
propose; the feedback aggregates already leave shadow proposals out.

| Breaking change | Required action |
|---|---|
| A shadow run that proposes an unchanged finding within the earlier shadow proposal's lifetime records no new shadow proposal, and its rules receive `AlreadyOpen`. | None, unless a report counted every shadow proposal; count the `AgentProposalDeduplicated` events of shadow threads for the repeats. |

## NewHeap.Platform.AI.Chat.AspNet: staleness in the decision facts and at the decision

`NhAssistantProposalDecisionFacts.SubjectChanged` (`bool?`, an init-only property) says whether the
object a pending proposal waits at changed since the proposal, by the precondition the execution runs
(`INhAiProposalPrecondition`; by default the recorded and current subject versions): `null` when the
proposal is not pending or the default precondition has no version to compare. When the proposal's
approval policy requires an unchanged subject (`RequireUnchangedSubject`), approving a proposal whose
object changed, through `POST approvals/{id}/decisions` or `INhAssistantProposalDecisionService.DecideAsync`,
fails with `assistant-proposal-stale` and closes it as stale as the execution would: the asker of a
consultation is told, and a deferred proposal's follow-up run starts when its policy asks for one. A
rejection is recorded as before.

| Breaking change | Required action |
|---|---|
| Approving a proposal of a changed object whose policy requires an unchanged subject fails with `assistant-proposal-stale` (HTTP 409) and closes the proposal, instead of succeeding and closing it as stale before execution. | Handle `assistant-proposal-stale` at the decision; read `SubjectChanged` from the decision facts to tell the person before they decide. |

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: hosts that know schedules but do not run them

`NhAssistantOptions.RunSchedules` (`NewHeap:AI:Assistant:RunSchedules`, true by default) says whether a
host runs the scheduled triggers. A host with `false` registers no recurring job for the schedules in
code, its data schedule sync adds, changes and removes no recurring job, and it needs no
`IRecurringJobManager`; triggers, runs, the administration and the following of data changes work as
usual. Keep it true on the hosts whose Hangfire servers run the schedules, with the same agents and
schedules, and set it to false on every other host that shares the database, such as an API host next
to the worker or a host without a Hangfire server, whose sync would otherwise remove the jobs of data
schedules its snapshot does not know.

| Breaking change | Required action |
|---|---|
| None; the default runs the schedules as before. | None. A host that removed the assistant's schedule registrations by their internal types sets `RunSchedules` to `false` instead. |

## NewHeap.Platform.AI.Chat, NewHeap.Platform.AI.Chat.SqlServer, NewHeap.Platform.AI.Chat.PostgreSql and NewHeap.Platform.AI.Chat.AspNet: the run a trigger event started

`NhAssistantTriggerReceipt.InputId` (an init-only property) names the queued input that holds the event:
for a started, queued, superseded, coalesced and waiting fire, and for a deferred fire the input it writes
once the application's transaction committed. `INhAssistantAgentRunLookup.FindByEventAsync(agentId,
triggerId, eventKey)` and `FindByInputAsync(inputId)` return that input as `NhAssistantTriggerEventRun`
with its thread, subject and whether it waits, and the run that consumed it, or while it waits the run
the event started. The lookup goes through the queued inputs, so an event that coalesced into another
event's run finds that run, and a redelivered event finds its first delivery. It is a server API for the
application and checks no person's access; with a tenant it finds only that tenant's work, and the whole
application's work for a tenant that takes part (`UseSingleTenant`). The migration `TriggerEventLookup`
adds the index `IX_AssistantQueuedInput_EventKey_TriggerId` on SQL Server and PostgreSQL.

| Breaking change | Required action |
|---|---|
| The assistant schema gains the migration `TriggerEventLookup` (one index). | Apply it with `RunMigrations` or in the deployment step that applies the assistant's migrations. |
| `NhAssistantTriggerReceipt` gains `InputId`, which takes part in its record equality. | Compare receipts by `Outcome`, `RunId` and the other properties instead of with a receipt built by hand. |

## NewHeap.Platform.AspNet.Common and NewHeap.Platform.AI.Chat.AspNet: the query token at the configured hub paths

`MapNewHeapAssistant` maps the assistant hub on the host's root at `NhAssistantOptions.HubPath`
(`/hub/assistant` by default, `NewHeap:AI:Assistant:HubPath`), outside the route prefix. NewHeap's bearer
setup reads `access_token` and `divisionId` from the query string below `/hub`, as before, and now also
below the configured path of a hub a NewHeap library maps: the assistant hub and the background-operations
hub while it is mapped. A hub moved to `/hubs/assistant` or under the route prefix connects with a query
token; every other request still takes its token from the `Authorization` header only.

| Breaking change | Required action |
|---|---|
| None; the default path stays. | None. A host that moved the assistant hub out of `/hub` and read the query token itself may remove that workaround. |

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: MCP servers from the application's code

`INhAssistantMcpServers` manages MCP servers, their tools and their agent assignments from the
application's own code, with the checks of the `admin/mcp-servers` endpoints: `ListAsync`, `CreateAsync`
and `UpdateAsync` with `NhAssistantMcpServerRequest` (and `NhAssistantMcpOAuthRequest` and
`NhAssistantMcpReaderRequest`; `null` keeps a stored value, `""` clears it), `DeleteAsync`, `TestAsync`,
`SyncAsync`, `ListToolsAsync`, `UpdateToolAsync` with `NhAssistantMcpToolSettingsRequest`, and
`AssignAsync` and `UnassignAsync`. Secrets are protected with Data Protection and never returned, logged
or audited; the audit is the administration's, with the actor the application names. An assignment
writes only the assignment, so the agent's definition and version stay and neither an open editor nor a
package import conflicts; it is audited as the new `AdminMcpServerAssignmentChanged` (`assigned` or
`unassigned`), at most `NhAssistantMcpServerLimits.MaxServersPerAgent` (32) servers per agent. The
service does not authorize the caller.

| Breaking change | Required action |
|---|---|
| None; `INhAssistantMcpServers` and `NhAssistantAuditEventKind.AdminMcpServerAssignmentChanged` are new. | Replace code that writes MCP server, tool or assignment rows with the service; let audit sinks that map every kind accept the new one. |

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: loopback MCP servers outside Development

`NhAssistantMcpOptions.AllowLoopback` (`NewHeap:AI:Assistant:Mcp:AllowLoopback`, off by default) lets a
host outside Development, such as a test host of the environment `Testing`, connect MCP servers and
their OAuth URLs on loopback, also over plain http with `RequireHttps`. A Production host with the option
on does not start. Link-local addresses and cloud metadata hosts stay blocked. Where loopback is allowed,
the OAuth `RedirectUri`, `ClientUri` and `ReturnOrigins` may be http on loopback too, as in Development.

| Breaking change | Required action |
|---|---|
| None; without the option loopback stays limited to Development. | Set the option in the configuration of test hosts that run MCP servers on loopback instead of running them as Development. |

## NewHeap.Platform.AI.Chat.AspNet: which skills and agents name a text asset

`INhAssistantInstructionAssetUsage.ListAsync(assetId)` lists every version of the data skills (agent,
skill id, version, status) and the agents created in the administration that name a text asset of the
application, with the asset version and hash each one uses (`NhAssistantInstructionAssetUsageDto`). An id
that is not an asset id fails with `assetId: invalid`. It only reads: a new version of the asset still
never takes effect by itself.

| Breaking change | Required action |
|---|---|
| None. | None. |

## NewHeap.Platform.AI.Chat.AspNet: the installed package and an agent's parts without an export

`INhAssistantAgentPackages.GetPackageAsync(packageId)` returns the package the host imported
(`NhAssistantInstalledAgentPackageDto`: version, format version, hash, agents, imported at and by) with
the current links of its parts per agent, also after every agent was unlinked. `ReadAsync(agentIds)`
returns the agents' parts and hashes exactly as `ExportAsync` builds them, without locks and without the
`AdminAgentPackageExported` audit.

| Breaking change | Required action |
|---|---|
| `INhAssistantAgentPackages` gained `GetPackageAsync` and `ReadAsync`. | A class of the application that implements the interface, such as a test double, implements both; read the installed version with `GetPackageAsync` instead of `GetLinkAsync`, and compare agents with `ReadAsync` instead of an export. |

## NewHeap.Platform.AI.Chat, NewHeap.Platform.AI.Chat.SqlServer, NewHeap.Platform.AI.Chat.PostgreSql and NewHeap.Platform.AI.Chat.AspNet: several searchable values per metadata entry

`IndexByEach(value => items)` on `NhAssistantAgentMetadataBuilder<T>` declares several searchable values
of an entry, such as the departments an agent works for. Each value is trimmed, in lowercase and stored
once, at most `NhAssistantAgentMetadataLimits.MaxIndexValuesPerEntry` (50) of at most 256 characters
(`value: too-long` otherwise, a failed start for a value in code), in the new table
`AssistantAgentMetadataIndexValue`, written in the same save as the value and removed with it.
`INhAssistantAgentMetadata.ListAgentsAsync(name, value)` matches any of them, stored or in code;
`NhAssistantAgentMetadataValue.IndexValues`, `NhAssistantAgentMetadataEntryDto.IndexedByEach` and
`NhAssistantAgentMetadataDto.IndexedByEach` and `IndexValues` show them. An entry declares `IndexBy` or
`IndexByEach`, never both. Values stored before the entry declared `IndexByEach`, or before its selector
changed, get their values when the process first reads the agents; `IndexBy` values still change only on
the next save. The migration `MetadataIndexValues` adds the table on SQL Server and PostgreSQL.

| Breaking change | Required action |
|---|---|
| The assistant schema gains the migration `MetadataIndexValues` (the table `AssistantAgentMetadataIndexValue`). | Apply it with `RunMigrations` or in the deployment step that applies the assistant's migrations. |
| `NhAssistantAgentMetadataValue` gains `IndexValues` and `NhAssistantAgentMetadataEntryDto` gains `IndexedByEach`, which take part in their record equality. | Compare values read from the assistant by their properties instead of with a record built by hand that leaves them out. |

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: agent definitions from code and agents as in effect

`INhAssistantAgentDefinitions` changes agent definitions from the application's code with the checks,
versions, package locks and audit of the administration: `UpdateAsync(agentId, request, expectedVersion,
actorId)` with `NhAssistantAgentDefinitionRequest`, `ResetAsync` and `DeleteAsync`, for agents created at
runtime and overrides of agents in code. The request replaces the display name, description,
instructions, tool selectors, autonomy and required policy, which must be a registered policy
(`requiredPolicy: not-found`), and keeps every optional field, also the agent's MCP assignments, unless it
names them; the administration's `PUT admin/agents/{id}` still replaces the assignments.
`INhAssistantAgentDirectory` (`GetAsync`, `ListAsync` with `NhAssistantAgentDirectoryFilter`) returns
agents as they are in effect (`NhAssistantAgentInEffectDto`): source, name and description in effect,
instructions resolved from an asset or the asset's problem, tool selectors, autonomy, profile, run state
with `NhAssistantAgentState`, shadow and `ShadowOf`, and metadata. Both are server APIs that authorize
nothing; the directory returns instructions in full.

| Breaking change | Required action |
|---|---|
| None; `INhAssistantAgentDefinitions` and `INhAssistantAgentDirectory` are new, and `INhAssistantAgentConfiguration` is unchanged. | Replace code that writes agent rows or calls the administration's agent endpoints with the services, and send the version you read with a change. |

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: promoting a shadow variant onto its live agent

`INhAssistantAgentDefinitions.PromoteShadowVariantAsync(variantId, request, actorId)` with
`NhAssistantShadowVariantPromotionRequest` (`ExpectedVersion`, `MoveSkills`, `ExpectedVariantVersion`,
`Reason`) and `POST admin/agents/{id}/promote` for administrators copy a shadow variant created as data
onto its live agent as the live agent's next version through the update path, then delete the variant,
which never goes live itself. The live agent keeps its display name, MCP assignments, metadata, run
settings, triggers, state and shadow mode; an agent in code takes only the fields it can override, an
agent created at runtime also the variant's profile, classification, what it works on and its asset.
With `MoveSkills` the variant's data skills become the next live versions of the live agent's skills of
the same id, or new skills, which start in shadow. New refusals: `assistant-not-shadow-variant` (409),
and `assistant-shadow-variants-not-allowed`, `assistant-version-conflict` and `assistant-skill-in-code`
before anything changes. The audit records the new `AdminAgentShadowVariantPromoted` (91) after the
events of each step. `NhAssistantShadowVariantDto.Version` is the variant's version a promotion expects.

| Breaking change | Required action |
|---|---|
| `NhAssistantAuditEventKind.AdminAgentShadowVariantPromoted` and the code `assistant-not-shadow-variant` are new. | Let audit sinks and error mappings that handle every kind or code accept them. |
| `NhAssistantShadowVariantDto` gains `Version`, which takes part in its record equality. | Compare variants read from the assistant by their properties instead of with a record built by hand. |

## @newheap/platform-ai-chat: agent names in the administration and the region refusal

The agent editor's title, the import dialog's name of a package agent the host does not have yet, and the
names in the agent list, the export dialog, the run configuration and the flow all read an agent's name
through `agentDisplayName`: an agent in code shows its translation in the reader's language instead of
its translation key. New texts translate the server's `assistant-model-region-denied` error and the field
error `region-not-permitted` (English and Dutch).

| Breaking change | Required action |
|---|---|
| The new translation keys `nh-assistant.errors.assistant-model-region-denied` and `nh-assistant.admin.run-config.field-errors.region-not-permitted`. | An application that replaces the bundled translations adds both keys in each of its languages. |

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: proposals on their way in the inbox and in counts

`GET approvals/inbox` takes `status=`: `pending` (the default, unchanged), `awaiting-application` for
approved proposals of tools the application executes that wait for its report, listed by report deadline
until it falls due, and `execution-unknown` for proposals whose execution is uncertain, listed by decision
until an approver resolves them (`NhAssistantInboxStatuses`). Each status keeps the inbox's visibility:
the member snapshot, the run's initiator and the whole application's live approvers. Another value is
`400 assistant-message-invalid`, and `NhAssistantInboxDto.Status` says which status the page lists.
`INhAssistantProposalCounts.CountAsync(person, statuses)` counts per status with the inbox's own query for
a person the application authenticated (`NhAssistantDecidingPerson`), for digests: ids and counts only.

| Breaking change | Required action |
|---|---|
| None. | None. |

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: the people of the whole application

`UseSystemSubject(system => system.Members<TMembers>())` registers the application's
`INhAssistantSystemMembers`, which lists the actor ids of the whole application app-wide and per division.
Notifications about agent work in a thread of the whole application consider the first 200 of them next to
the thread's participants and deciders, each with the role the read and approve policies and the agent's
view policy give them now, so a company-wide proposal in a thread nobody opened reaches its approvers and
never its readers. Without it nothing changes.

| Breaking change | Required action |
|---|---|
| None. | None. |

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: inline approvals of tools the application executes

`ExecuteInApplication(toolId, reportWithin, execution => execution.AlsoInline())`
(`NhAssistantApplicationExecutionBuilder`) hands a person's inline approval in their own conversation, a
chat or a request in an agent thread, to the application's executor too, instead of running the tool's
function: the proposal waits as `awaiting-application` until the report deadline, the hand-off is audited,
`NhAssistantProposalHandOff.Inline` is set, and the turn goes on with a tool result that says the
application has it (`assistant-proposal-handed-to-application`), refused it or may not have taken it. In a
chat the report, a refused hand-off and a passed deadline are posted into that conversation as an
untrusted application message with the outcome and its code; nobody is notified there and no follow-up run
starts. The run maintenance also expires such report deadlines in a host without agent runs. Without
`AlsoInline` an inline approval still runs the tool's function. The person who approved a chat proposal
resolves its uncertain hand-off with `POST conversations/{id}/approvals/{approvalId}/resolve`
(`hand-to-application` with a new attempt and deadline, or `mark-resolved`), with the rules of an agent
thread's resolve and the audit `AgentProposalResolved`; the chat's approval part says so with
`CanResolve`, next to `ExecutionResultCode` and `ExecutionReportDueAt`.

| Breaking change | Required action |
|---|---|
| None. | None. |

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: a consultation limit per turn

`NhAssistantLimits.MaxConsultationsPerTurn` (1 to 100, unlimited by default) bounds the consultations one
turn starts, and `NhAssistantAgentRunBuilder.MaxConsultationsPerTurn(count)` replaces it for that agent's
turns. Every consultation the turn starts counts; the next one is refused before the consulted agent runs
with the new code `assistant-consult-turn-limit` (HTTP 400), which the asking model reads.

| Breaking change | Required action |
|---|---|
| None. | None. |

## NewHeap.Platform.AI.Common, NewHeap.Platform.AI.AspNet.Common, NewHeap.Platform.AI.AspNet.Mvc, NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: turns that outlive their client and a person's turns from the application's channels

`NhAssistantOptions.KeepTurnsAfterDisconnect` (`NewHeap:AI:Assistant:KeepTurnsAfterDisconnect`, off by
default) keeps a person's turn of `POST conversations/{id}/messages` and
`POST conversations/{id}/approvals/{approvalId}/decide` running after the client disconnected, up to
`NhAssistantLimits.TurnTimeout`. The turn no longer follows the request's cancellation; its tools
authorize the principal and the invocation context captured when it started, never the request again;
its remaining stream writes are dropped; the conversation's snapshot and live updates carry its result.
`POST cancel` still stops it. There is no resumable stream: the client reloads the conversation.

`INhAssistantConversationTurns` sends a person's message (`SendAsync` with
`NhAssistantConversationMessage`) and decides the approval their turn in a chat waits for (`DecideAsync`
with `NhAssistantConversationDecision`) for the application's own channels, for a person the application
authenticated (`NhAssistantDecidingPerson`). It applies the endpoints' rules: the person's conversation in
their tenant, the assistant's access policy and the agent's policy, the message and decision rules. The
person's context comes from the application's context resolver and must name the same actor and tenant
(`assistant-context-unavailable` otherwise); `NhAssistantConversationTurnOptions` names the audited
`Channel`, the `Language`, the person's `ActiveDivisionId` and whether the turn outlives the caller
(`KeepTurnAfterCallerLeaves`, by default the host's `KeepTurnsAfterDisconnect`). The turn runs in services
of its own and without the caller's request, so its tools authorize the person and no request credential
reaches them; API bridge tools and MCP servers that forward the person's sign-in need the person's own
request and are not available. It returns `NhAssistantConversationTurnResult` with the status, the
assistant's message, the pending approvals and the usage. Only chats are served.

| Breaking change | Required action |
|---|---|
| None; `KeepTurnsAfterDisconnect` is off by default and `INhAssistantConversationTurns` is new. | Set the option where closing the browser should not cancel a turn and let the client reload the conversation; start a person's turns from a channel through the service instead of the endpoints. |
| `NhAiAspNetScopeAuthorizationResource` is no longer `sealed`; in a person's turn that does not depend on the request the gate passes a derived resource that also implements `INhDivisionScopedResource` with the person's active division. | None for handlers that match the record; a division handler reads the division from the resource. |
| `NhAssistantAuditEvent.Channel` is also set on the events of turns the service runs, such as `ToolInvoked` and `ApprovalRequested`. | Let audit sinks that expect a channel only on decisions accept it on other events. |

## @newheap/platform-ai-chat: the inbox status filter and the consultation limit

`AgentInboxFilter.status` (`AgentInboxStatus`: `pending`, `awaiting-application`, `execution-unknown`)
passes `status=` to `GET approvals/inbox`, and `AgentInbox.status` names the status the page lists; the
mock API serves both. `NH_ASSISTANT_SERVER_ERROR_CODES` gains `assistant-consult-turn-limit`, translated in
English and Dutch. In a chat, the approval card of an uncertain hand-off the user approved
(`ApprovalPart.canResolve`, with `executionResultCode` and `executionReportDueAt`) offers to hand it to the
application again or to mark it done: the card's `resolve` output (`ApprovalResolution`), the thread's
`approvalResolution` (`NhAssistantApprovalResolutionChoice`), `NhAssistantStore.resolveApproval` and
`NhAssistantApiService.resolveApproval` (`ResolveApprovalRequest`); the mock API resolves it as the server
does.

| Breaking change | Required action |
|---|---|
| The new translation keys `nh-assistant.errors.assistant-consult-turn-limit` and `nh-assistant.approval.resolve.explanation`, `hand-to-application`, `mark-resolved` and `resolving`. | An application that replaces the bundled translations adds the keys in each of its languages. |

## @newheap/platform-ai-chat: promoting a shadow variant in the administration

The shadow variants of an agent in the administration offer "Promote onto {agent}" for a data variant,
with a confirmation that says what the live agent keeps and an option to move the variant's skills. It
calls the new `NhAssistantAdminApiService.promoteShadowVariant(variantId, request)` with
`ShadowVariantPromotionRequest` and `ShadowVariantPromotion`, at the live agent's version and the variant's
(`ShadowVariant.version`). The mock API promotes as the server does. `AgentMetadataEntry` gains the
optional `indexedByEach` of `IndexByEach`, and `AgentMetadata` also its `indexValues`.

| Breaking change | Required action |
|---|---|
| New translation keys `nh-assistant.errors.assistant-not-shadow-variant` and `nh-assistant.admin.shadow-variants.promote`, `promote-named`, `promote-confirm.*` and `promoted`. | An application that replaces the bundled translations adds the keys in each of its languages. |

## @newheap/platform-common: modals for the keyboard and screen readers

`nh-modal` owns its accessibility. The holder is a dialog with `aria-modal="true"`, named by its title
through `aria-labelledby`, and the close button is a `type="button"` with the translated label
`nh-common.modal.close` (English and Dutch in the new `NH_COMMON_TRANSLATIONS`; an application's own
translation of the key wins). Opening moves focus to the content's `autofocus` element, else its first
control, else the close button or the dialog; content that renders its fields after loading gets its
first field focused once it appears, and `focusInitial()` does so again on request. Tab and Shift+Tab
stay inside the topmost modal and skip disabled and hidden controls; closing, also by a route change,
gives focus back to the element that opened the modal. Escape closes only the topmost modal that can
close: content with the new optional `INhModalComponent.onModalEscape` decides, and mutating and
confirmation content call `closeDialog` there. The `nh-modal-open` body class stays until the last open
modal closes.

| Breaking change | Required action |
|---|---|
| `NhModalMutateBaseTypeComponent` and `NhModalConfirmComponent` no longer listen to `document:keyup`; the shell handles Escape on keydown for the topmost modal and calls `onModalEscape`, so a stack closes from the top and `onKeyUp` (deprecated) is no longer called. | Override `onModalEscape` or `closeDialog` instead of `onKeyUp`, and remove own Escape listeners and focus traps from modal content. |
| A modal opened with `closeable: false` no longer closes on Escape, also with mutating or confirmation content. | Offer an explicit action in such a modal when people must be able to leave it. |
| The modal takes focus when it opens and gives it back when it closes. | Remove code that focused a field after opening a modal unless it chooses another field; mark the field with `autofocus` instead. |

## @newheap/platform-ai-chat: documents with relative links and diagrams

The new standalone `nh-assistant-document` (`NhAssistantDocumentComponent`) renders untrusted Markdown,
such as the files of a skill or knowledge bundle, with the assistant's safety defaults: inline HTML as
text, images as their alt text, and only links to its own headings, `https:`, `http:` and `mailto:`
addresses or other documents of the bundle, which `siblingPaths` lists and relative links resolve
against from `path`. `linkClick` (`NhAssistantDocumentLinkEvent`) reports a link without navigating to a
sibling, `headingsChange` (`NhAssistantDocumentHeading`) the headings, and `scrollToHeading` scrolls to
one. An optional `NhAssistantDiagramRenderer` the application supplies, and loads lazily inside
`render`, draws fenced blocks of its languages; the document sanitizes the SVG, shows it in a shadow
root, and shows a block it cannot draw as source with the reason. No package dependency was added.

| Breaking change | Required action |
|---|---|
| New translation keys `nh-assistant.document.diagram-failed`, `diagram-no-reason` and `diagram-not-svg`. | An application that replaces the bundled translations adds the keys in each of its languages. |

## newheap-platform: a public website and Angular pins from the peer range

The consumer bootstrap and inspector accept the profile `public-website`: an API with a public Angular
front end, without sign-in (the bootstrap refuses `--authentication` with it) and with persistence only
when chosen. Its manifest records `frontend: "public"`, and the inspector's validate mode drops the
portal's identity store, API base and modal requirements for it. The Angular framework pins of both
front-end profiles are now the lowest version that every `@angular/*` peer range of
`@newheap/platform-common` accepts (today 20.3.28, which the old pin 20.3.27 did not satisfy), read from
the new `npmPeerDependencies` of `distribution.json` and the install lock.

| Breaking change | Required action |
|---|---|
| The bootstrap writes Angular 20.3.28 instead of 20.3.27 into `src/Front-end/package.json` and refuses to overwrite a different file. | Before rerunning the bootstrap in a consumer bootstrapped earlier, raise its Angular packages to the generated pins; `@newheap/platform-common` requires `^20.3.28` anyway. |
| A front-end bootstrap needs `npmPeerDependencies` in the compatibility metadata. | Reinstall the skill from this plugin version before bootstrapping a front end. |
