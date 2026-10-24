# v-next

## NewHeap.Platform.AI.Chat, NewHeap.Platform.AI.Chat.AspNet, NewHeap.Platform.AI.Chat.SqlServer and NewHeap.Platform.AI.Chat.PostgreSql: a tool selection per consultation

`AllowConsultation(subjectType, consult => consult.Tools("projects.search", ...))` names the tools the
consulted agent is offered in that consultation, by id or `prefix.*` selector; each must be covered by
the agent's own selectors, or startup fails, as for a skill's tools. The consulted model sees only those
tools, a call of another tool runs nothing and returns the new code
`assistant-consult-tool-not-selected`, and a consultation tool of another agent (`assistant.consult.*`,
`assistant.request.*`) is offered only when a selector names it; effects, routes and the depth limit
apply as before. A consultation defined as data stores its selection as `ToolSelectors` of
`NhAssistantDataConsultationRequest` and `NhAssistantDataConsultationDto`, which also reports
`ToolSelectorsInEffect`: saving refuses a selector outside the agent's selectors with
`toolSelectors: not-allowed`, and a selector the agent's selectors no longer cover drops on load with the
new problem `tools-not-allowed`, leaving the consultation without any tool when none is left, never
with every tool of the agent. `NhAssistantConsultationOptions.ToolSelectors` and
`NhAssistantCodeConsultationDto.ToolSelectors` show the selection; a change of it is the consultation's
next version. Without a selection a consultation offers every tool of the agent, as before, and the
consult tool's contract does not change.

| Breaking change | Required action |
|---|---|
| The `ConsultationToolSelectors` migrations add the nullable column `AssistantAgentConsultationDefinition.ToolSelectorsJson` on SQL Server and PostgreSQL. | Apply the assistant migrations before the new version starts (`RunMigrations`, or your deployment's migration step). |
| `NhAssistantErrorCodes` has the new code `assistant-consult-tool-not-selected` (HTTP 400), and `NhAssistantRunConfigurationProblems` the new problem `tools-not-allowed`. | Let code that maps assistant codes or consultation problems handle values it does not know. |

## @newheap/platform-ai-chat: a tool selection per consultation

The consultation form of the run configuration offers the agent's own tool selectors as check boxes
under Advanced, none checked offering every tool of the agent; a saved tool the agent's selectors no
longer cover stays checked with the reason until it is cleared, and `toolSelectors: not-allowed` shows at
the tools. Each row of the Consultations section names the consultation's tools. `DataConsultation`
(`toolSelectors`, `toolSelectorsInEffect`), `DataConsultationRequest` and `CodeConsultation` carry
`toolSelectors`; the problem `tools-not-allowed`, its field error and the code
`assistant-consult-tool-not-selected` have English and Dutch texts. The mock API applies the same checks
and takes `consultationTools` for consultations in code.

| Breaking change | Required action |
|---|---|
| The text `nh-assistant.admin.run-config.consultations.form.advanced` names the tools as well. | Update your application's override of this translation key. |
## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: one notification per person and run

Everything of one run that waits for a person's decision, in the run's own thread and at the objects it
placed proposals at, now reaches that person as one notification, and the run maintenance bundles the
reminders and escalations of one pass per run and person. `INhAssistantAgentNotificationPolicy` is still
asked per object (`RunEndedWithProposals`) or per proposal (`ProposalExpiring`, `ProposalEscalated`)
with the context of before, so custom policies keep working; Web Push, the in-app channel and every
`INhAssistantAgentNotificationChannel` then receive one context of a new kind per group of recipients
with the same objects, whose `Objects` name only what waits for them. A click opens the run's own
thread for its initiator and otherwise the object with the most proposals; the payload also carries the
run, which `@newheap/platform-ai-chat` hands to the application. Proposals a consultation leaves at its
subject arrive the same way, without a run.

| Breaking change | Required action |
|---|---|
| The proposals of a run reach Web Push and the channels as `NhAssistantAgentNotificationKind.RunProposalsWaiting`, `RunProposalsExpiring` and `RunProposalsEscalated` (8 to 10), one per group of recipients with the same objects; `RunEndedWithProposals`, `ProposalExpiring` and `ProposalEscalated` no longer reach them. | Handle the new kinds in a custom `INhAssistantAgentNotificationChannel` and its exhaustive switches; keep handling the old kinds in a custom notification policy. |
| `NhAssistantAgentNotificationContext` has `Objects` (`NhAssistantAgentNotificationObject`: subject, display name, conversation and proposal count); for the new kinds `ConversationId`, `Subject` and `SubjectDisplayName` name the object a click opens, `ProposalCount` counts all objects, `ApprovalId` and `ToolId` are set only for exactly one proposal and `ExpiresAt` is the earliest expiry. | Read the objects of a notification from `Objects` instead of one subject per notification. |
| Notifications about several objects read "Assistant" ("Assistent") and "N proposals at M projects wait for your decision", with the subject type's plural name in the recipient's language, or "objects" when the types differ or have no plural; reminders and escalations of several proposals are counted the same way. | Give subject types a plural with `NhAssistantSubjectTypeBuilder.NamedPlural`. |
| The Web Push payload of the new kinds carries `runId` and the tag `nh-assistant-run:<runId>`, its kinds are `agent-run-proposals`, `agent-run-proposals-expiring` and `agent-run-proposals-escalated`, and a newer notification about the same run replaces an undelivered older one. | Copy the push worker of `@newheap/platform-ai-chat` into the application's assets again. |
| The in-app message about the proposals of a run groups per run (`assistant-agent:run:<runId>`) instead of per agent thread. | None, unless the application reads `GroupKey`; then expect the run's group. |

## @newheap/platform-ai-chat: open the run of a notification

`NhAssistantPushService.openRun$` emits the run of a clicked notification about the proposals of a run,
so the application opens its inbox of that run, for example `nh-assistant-agent-inbox` with `[runId]`.
The bundled worker forwards the run with the click, and a click that opens a new window does so through
`#nh-assistant-run=<runId>.<conversationId>` (`NH_ASSISTANT_RUN_FRAGMENT`). Without a subscriber a click
opens the conversation in the panel as before.

| Breaking change | Required action |
|---|---|
| While the application subscribes to `NhAssistantPushService.openRun$`, a clicked notification about the proposals of a run emits its run there instead of opening the conversation in the panel. | Subscribe in the application shell and open the inbox of the run; copy `push/nh-assistant-push-worker.js` into the assets again, since only the new worker forwards the run. |

## NewHeap.Platform.AI.AspNet.Common: an agent's rights source covers the tool's capabilities

The agent invocation gate lets the initiator a turn delegates from (`NhAiGrantSource.Delegated`) and the
account an agent answers to (`NhAiGrantSource.Accountable`) lend their rights for a tool only when they
pass the tool's policies and their capability grants (`AddCapabilityGrant`) also cover the tool's
`RequiredCapabilities`, compared by name as the invoker compares them. One that passes the policies
without a capability now lends nothing, and the decision continues as for a missing policy: initiator,
account, then the agent's grant. An agent in code whose technical account misses a capability's policy
therefore runs the tool through its grant instead of being refused, and an agent is no longer offered
a tool that only such a source would allow. When no source fits and one failed only on a capability,
the refusal is `ai-tool-capability-denied` instead of `ai-tool-agent-not-granted`. No source gains
rights it did not have.

| Breaking change | Required action |
|---|---|
| An initiator or account that passes a tool's policies but whose capability grants do not cover its `RequiredCapabilities` no longer counts as `Delegated` or `Accountable`; the agent's grant decides, and without one the call is refused with `ai-tool-capability-denied` (`NhAiToolFailureCodes.CapabilityDenied`) and the tool is not offered. | Register each capability with the policy of the right the tool needs, and give the account that policy where it should lend its rights; expect `ai-tool-capability-denied` where you handled `ai-tool-agent-not-granted`. |

## NewHeap.Platform.AI.AspNet.Common and NewHeap.Platform.AI.Chat.AspNet: rights outside a request from the application's access source

A host whose rights come from a claims transformation per request, such as an OIDC host that adds the
application's own permissions, implements `INhAiAccessSource.GetAsync(actorId, subject)`, which returns
an `NhAiAccessContext` with the claims the application last knew and when they were valid (`ValidAt`),
or `null`, and calls `UsePrincipalsFromAccessSource<TSource>(o => o.MaxAge = ...)` on the AI ASP.NET
builder. It registers an `INhAiPrincipalFactory` in place of the one that reads the NewHeap user store,
so every decision outside a person's request uses those claims: runs that delegate from their initiator
and their follow-up runs, consultations, the account an agent answers to, MCP connections in the
background and the whole application's people. No context, a context older than `MaxAge` on the
application's clock (the registered `TimeProvider`), or one whose tenant claim does not name the
subject's tenant where tenants come from a claim, yields no principal, and the work is refused with the
usual codes. The principal carries exactly the context's claims (authentication type
`nh-ai-delegation`, with `NameClaimType` and `RoleClaimType` from the options); NewHeap stores nothing.
`MaxAge` is required and positive, a principal factory of the host's own cannot be combined with it,
and the MCP owner sweep removes no connection on the source's answer.

| Breaking change | Required action |
|---|---|
| None; the access source is opt-in, and a host without `UsePrincipalsFromAccessSource` keeps the built-in or its own `INhAiPrincipalFactory`. | None. To use it, implement `INhAiAccessSource`, call `UsePrincipalsFromAccessSource<TSource>(o => o.MaxAge = ...)` with a positive `MaxAge`, and remove a principal factory of your own, or startup fails. |

## NewHeap.Platform.AI.Common and NewHeap.Platform.AI.Chat: the real code of a refused tool call

The invoker records the code of every refusal it audits as `NhAiOutcomeKind.AuthorizationDenied` as the
audit record's `ResultCode`: the invocation gate's own code, `ai-tool-capability-denied`,
`ai-tool-effect-denied` and `ai-tool-approval-delegation-invalid`. The assistant uses that code before
the outcome kind, so a conversation's tool call (`resultCode`), the `ToolInvoked` audit event, the
model's summary of the call and the result code of an executed proposal name the real reason.

| Breaking change | Required action |
|---|---|
| `NhAiAuditRecord.ResultCode` of an `AuthorizationDenied` record is the refusal's code instead of `null`. | Read the reason of a refusal from `ResultCode`; keep `Outcome` for its kind. |
| Assistant tool calls, `ToolInvoked` audit events and proposal execution results carry the refusal's own code (such as `ai-tool-agent-not-granted`, `ai-tool-capability-denied`, `ai-tool-effect-denied`, `ai-tool-approval-delegation-invalid` or an authoritative denial's code) instead of `ai-tool-authorization-denied` or `ai-tool-execution-evidence-invalid`. | Update filters, reports and translations that match `ai-tool-authorization-denied` to the codes you handle; `ai-tool-authorization-denied` remains for a gate that refuses without a code of its own, such as a person who misses the tool's policy. |

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: a decision service for application channels

`INhAssistantProposalDecisionService` decides proposals of agent threads for the application's own
channels, such as a reply to a message that asked an approver. It is a server API whose caller vouches
for the person: the application passes the principal and actor id it authenticated
(`NhAssistantDecidingPerson`). `GetDecisionFactsAsync` returns the current proposal hash, the expiry,
whether four eyes apply, the approvals still needed and whether the person may decide, with the refusal
code otherwise, and no proposal content (`NhAssistantProposalDecisionFacts`).
`DecideAsync(person, approvalId, expectedHash, decision, channel, idempotencyKey)` is the decision of
`POST approvals/{id}/decisions` through the same implementation, with the same checks, effects and
refusal codes; proposals decided in a conversation are refused as there. The channel is a dash-case code
(`NhAssistantDecisionChannels`) and the idempotency key at most 100 printable characters without white
space; both reach only the content-free audit. A repeat by the same person with the same decision and
hash returns the earlier decision with `NhAssistantProposalDecisionResult.Duplicate` and is not audited
again; anything else gets the ordinary refusal. No migration; the decision endpoints answer as before.

| Breaking change | Required action |
|---|---|
| `NhAssistantAuditEvent` has the new properties `Channel` and `IdempotencyKey`; `ApprovalApproved` and `ApprovalRejected` of agent proposals carry `Channel` `web` from the decision endpoints, or the service's channel and key. | Let audit sinks that map every property of the event store or ignore the two new values. |
## NewHeap.Platform.AI.Common, NewHeap.Platform.AI.Chat, NewHeap.Platform.AI.Chat.AspNet, NewHeap.Platform.AI.Chat.SqlServer and NewHeap.Platform.AI.Chat.PostgreSql: run facts for content-free usage

`AgentRunEnded` carries the content-free facts an application needs to count runs from its business
audit sink alone: `TriggerKind`, what started the run as one of the new `NhAssistantTriggerKinds`
(`event`, `schedule`, `webhook`, `manual`, `message`, `request`, `hand-off`, `follow-up`, `replay`),
recorded on the run when it is created; `Shadow`; `SkillVersion` of a skill defined as data;
`ModelCallCount`, every governed model call of the run across its turns and attempts, triage included;
and `RunStatus` and `RunFailureCode` apart, while `ResultCode` keeps `status:code`. A run counts each
model call when its budget is reserved, so a turn that fails, is cancelled or is retried keeps its calls,
and a run by rules alone stays at 0. Every governed model call gets its own `NhAiUsageRecord.ModelCallId`,
which its retries, its budget reservation (`NhAiBudgetRequest.ModelCallId`) and its `ai.model.invoke`
activity (`newheap.ai.model_call_id`) share; `InvocationId` still names the invocation context.

| Breaking change | Required action |
|---|---|
| The `RunTriggerKinds` migrations add the nullable column `AssistantRun.TriggerKind` on SQL Server and PostgreSQL. | Apply the assistant migrations before the new version starts (`RunMigrations`, or your deployment's migration step). |
| `AssistantRun.ModelCalls`, and with it the run history's and the evaluation's model calls, also counts the calls of turns that failed, were cancelled or were retried. | Expect higher counts than before for runs with such turns; compare periods across the upgrade with that in mind. |
| `NhAssistantAuditEvent` has the new properties `TriggerKind`, `SkillVersion`, `Shadow`, `ModelCallCount`, `RunStatus` and `RunFailureCode`, set on `AgentRunEnded`. | Let audit sinks that store every property accept them; count runs from them instead of parsing `ResultCode`. |
| `NhAiUsageRecord` and `NhAiBudgetRequest` have the new property `ModelCallId`; a reservation that is no model call keeps `Guid.Empty`. | Count and correlate model calls by `ModelCallId` instead of `InvocationId`. |

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: a single-tenant mode for the whole application

The whole application's work (`nh-system`) belongs to no tenant, so a caller bound to a tenant saw none
of it. `NhAssistantBuilder.UseSingleTenant(tenantId)` declares that every person of the host belongs to
that one tenant: people of that tenant then take part in it as callers without a tenant do, in the
approval inbox, deciding (also through `INhAssistantProposalDecisionService` with
`NhAssistantDecidingPerson.TenantId`), boards and division visits, `WatchAgentWork`, its threads and
their conversations, the run history, the subject's work and summary, the division search and runs by
hand. A caller of any other tenant finds that work unknown. The policies of `UseSystemSubject` and the
agents' run and view policies still decide. The work stays stored without a tenant: no migration, and
switching the mode off leaves the data valid. Startup fails when the AI ASP.NET integration projects
another fixed tenant or takes an issuer's tenant from a claim; pair the mode with
`UseAuthenticatedClaimsForSingleTenant(issuer, tenantId)` for the same tenant. A resolver of the
application's own must return that tenant or none. Without the mode only callers without a tenant, such
as the host's operators, take part, now also where the work is reached by its id.

| Breaking change | Required action |
|---|---|
| A caller bound to a tenant can no longer decide, resolve, give feedback on or read the decision facts of a whole-application (`nh-system`) proposal by its id, start or open an agent's thread of the whole application, or read that thread, its runs, proposals and files or the subject's work and agents, unless `UseSingleTenant` names exactly that tenant; it answers `404` with the codes of unknown work. | Decide and run the whole application's work as an operator without a tenant, or call `UseSingleTenant(tenantId)` in a host whose people all belong to that tenant. |

## NewHeap.Platform.AI.Chat, NewHeap.Platform.AI.Chat.AspNet, NewHeap.Platform.AI.Chat.SqlServer and NewHeap.Platform.AI.Chat.PostgreSql: extensible agent metadata

`AddAgentMetadata<T>(name, entry => entry.MaxBytes(...).Validate(...).IndexBy(...).Editable())`
registers a named entry of the application's own data about agents, such as a cost center: `T` is its
JSON shape (camelCase), the size limit counts the bytes of its JSON (4,096 by default, at most 65,536),
and the validators report dash-case codes per field (`NhAssistantAgentMetadataErrors`). An agent in code
sets its value in `ConfigureAgent` with `Metadata(name, value)`, which startup checks as every save
checks data; administrators override it only when the entry is `Editable()`, as an override with a
version of its own. An agent created at runtime gets values through `metadata` of `POST` and
`PUT admin/agents`, `NhAssistantAgentRequest.Metadata` of `CreateAgentAsync` and
`PUT admin/agents/{id}/metadata/{name}` (`INhAssistantAgentConfiguration.SaveMetadataAsync`, with
`RemoveMetadataAsync` and `GetMetadataAsync`); each change is the agent's next version. Values live in
the new table `AssistantAgentMetadata` per agent and name, with the entry's searchable value
(`IndexBy`) trimmed and in lowercase in an indexed column, and are removed with the agent.
`INhAssistantAgentMetadata` reads an agent with its metadata (`GetAgentAsync`, typed with
`NhAssistantAgentWithMetadata.Get<T>`) and finds the agents of a searchable value with provider-neutral
LINQ (`ListAgentsAsync(name, value)`). NewHeap never interprets metadata and it never widens rights; the
audit records `AdminAgentMetadataChanged` with the entry's name only.

| Breaking change | Required action |
|---|---|
| The `AgentMetadata` migrations add the table `AssistantAgentMetadata` on SQL Server and PostgreSQL. | Apply the assistant migrations before the new version starts (`RunMigrations`, or your deployment's migration step). |
| `NhAssistantAuditEventKind` has the new value `AdminAgentMetadataChanged` (71), and `NhAssistantAuditEvent` the property `MetadataEntry`. | Let audit sinks with an exhaustive switch, or that store every property, record or ignore them. |
| `NhAssistantAdminErrorCodes` has the new codes `assistant-metadata-entry-not-found` (HTTP 404) and `assistant-metadata-read-only` (HTTP 409). | Let code that maps assistant codes handle values it does not know. |
| `INhAssistantAgentConfiguration` has the new members `GetMetadataAsync`, `SaveMetadataAsync` and `RemoveMetadataAsync`. | Implement them in a class of your own that implements the interface, or delegate to the registered one. |

## @newheap/platform-ai-chat: a slot per entry of agent metadata in the agent editor

Below its form, the agent editor of `@newheap/platform-ai-chat/admin` shows the application's fields of an
existing agent (`nh-assistant-agent-metadata`): one slot per entry of the run catalog's `agentMetadata`,
with its title, where its value comes from (code, override or stored with the agent) and whether only
code changes it. The application renders its own editor in a slot: `provideNhAssistantAgentMetadataEditors`
maps an entry name to a standalone component, which injects `NhAssistantAgentMetadataSlot` for the value
and saves with `save` and `remove`, through `NhAssistantAdminApiService.saveAgentMetadata` and
`removeAgentMetadata` with the entry's version; there is no generic form. A change of an agent created
in the administration is its next version, which the editor's next save continues on; a new agent shows
its fields once it is saved. `AgentMetadata`, `AgentMetadataEntry`, `AgentMetadataRequest`,
`AdminAgentInput.metadata` and `getAgentMetadata` complete the client. The slot's frame and the codes
`assistant-metadata-entry-not-found` and `assistant-metadata-read-only` have English and Dutch texts. The
mock API takes `admin.agentMetadata` and the agents' `metadata` of its scenario.

| Breaking change | Required action |
|---|---|
| The agent editor shows a section of application fields below its form when the application registers entries of agent metadata. | Register an editor per entry with `provideNhAssistantAgentMetadataEditors`; an entry without one says that the application shows no editor. |

## NewHeap.Platform.AI.Mcp and NewHeap.Platform.AI.AspNet.Common: MCP error messages keep their format and arguments

A failed result of the MCP export (`WithNewHeapPlatformAITools`, flat and enveloped, with or without a
typed payload) carries the composite format of its message and its arguments in `_meta` next to
`newheap.com/message`, so a client can translate the failure:
`NhAiMcpResultMetadata.MessageFormatKey` (`newheap.com/message-format`, such as
`Project '{0}' has {1} open tasks.`) and `MessageArgumentsKey` (`newheap.com/message-arguments`). The
arguments are exactly the values the text shows: strings, booleans and numbers as JSON primitives,
`DateTime`, `DateTimeOffset` and `DateOnly` as ISO 8601 to the second with the invariant culture, and the
shown text for an argument with a format specifier or of another type. Both keys come together, and only
when the format filled with the arguments gives the text exactly within `MaxMessageArguments` (16),
`MaxMessageArgumentLength` (256 characters) and `MaxMessageFormatLength` (1,024 characters); a message that
joins several messages or starts with a field name carries neither. A fixed message, such as a pipeline
refusal or a refusal of the export itself, is its own format without arguments; what an application's
resolver reports in place of a refusal stays shielded. The text `code: message`, the structured
`{ code, message }` and `newheap.com/message` do not change. `NhAiMcpToolException` exposes the keys as
`FailureMessageFormat` and `FailureMessageArguments` to a client, also a NewHeap host calling another
host's export.

| Breaking change | Required action |
|---|---|
| A failed MCP result carries two more `_meta` keys, `newheap.com/message-format` and `newheap.com/message-arguments`. | None, unless a client rejects or snapshots unknown `_meta` keys; then accept the two keys. |
| Only a message that reaches `TaskResult` as a `FormattableString` keeps its arguments; an interpolated string passed straight to `Failed`, `AddError` or `WithError` binds to their `string` overload and publishes its rendered text as its format, without arguments. | Declare a message that clients should translate as a `FormattableString` (`FormattableString message = $"..."`) before passing it to `TaskResult`. |

## NewHeap.Platform.AI.Chat, NewHeap.Platform.AI.Chat.AspNet, NewHeap.Platform.AI.Chat.SqlServer and NewHeap.Platform.AI.Chat.PostgreSql: proposals the application executes

`ExecuteInApplication(toolId, reportWithin)` declares that the application executes the approved deferred
proposals of a tool itself, outside the tool's function, and reports within `reportWithin` (at most 90
days); `UseProposalExecutor<TExecutor>()` registers the application's `INhAssistantProposalExecutor`, and
startup fails when a declared tool has none. Once a person approves such a proposal, by any path, the
execution's usual checks run, the proposal becomes the new status `awaiting-application` with
`NhAssistantProposalDto.ExecutionReportDueAt`, and the executor receives an `NhAssistantProposalHandOff`
(approval id, the approved `NhAiProposal` with its hash, the approver, agent, subject, tenant, attempt and
deadline). The application reports through `INhAssistantProposalExecutionReports.ReportExecutionAsync`
with an `NhAssistantExecutionReport`: `Completed` closes the proposal as `executed`, `Failed` as
`execution-failed` and `NeedsAttention` as `execution-unknown`, each with the application's dash-case code;
an optional summary of at most 2,000 characters is posted into the agent's thread as an application message
without the trusted flag. The report is audited with its outcome and code only, notifies the approvers and
starts the follow-up run once per hand-off; the same report again is a duplicate, and a report reaches only
the proposals of its tenant. Without a report in time the run maintenance makes the proposal
`execution-unknown` (`report-overdue`), and `POST approvals/{id}/resolve` hands it to the application again
with the new action `hand-to-application`. Runs and consultations always propose a declared tool deferred;
every tool that is not declared executes in its function as before.

| Breaking change | Required action |
|---|---|
| The `ApplicationExecutions` migrations add the nullable column `AssistantApproval.ExecutionReportDueAt` and an index on `(Status, ExecutionReportDueAt)` on SQL Server and PostgreSQL. | Apply the assistant migrations before the new version starts (`RunMigrations`, or your deployment's migration step). |
| `NhAssistantApprovalStatuses` has the new open status `awaiting-application`, which a proposal of a tool declared with `ExecuteInApplication` takes after its approval. | Let code that switches over proposal statuses treat it as approved and not yet executed. |
| `NhAssistantAuditEventKind` has the new value `AgentProposalHandedToApplication` (72), and `NhAssistantAuditEvent` the properties `ExecutionOutcome` and `ExecutionCode`, set on `AgentProposalExecuted` for the application's report. | Let audit sinks with an exhaustive switch, or that store every property, record or ignore them. |
| `NhAssistantAgentNotificationKind` has the new value `ExecutionReported` (11), sent as Web Push kind `agent-execution-reported`. | Let notification policies and channels with an exhaustive switch handle it; the default policy tells the subject's approvers. |
| `NhAssistantErrorCodes` has the new codes `assistant-execution-report-not-awaited` (HTTP 409) and `assistant-execution-report-invalid` (HTTP 400), and `NhAssistantConsultationRefusalReasons` the reason `application-executes`. | Let code that maps assistant codes or consultation refusal reasons handle values it does not know. |
| `POST approvals/{id}/resolve` takes the action `hand-to-application` for a proposal with `executionReportDueAt` and refuses `re-execute` for it with `400 assistant-approval-decision-invalid`. | Offer `hand-to-application` and `mark-resolved` for such a proposal, `re-execute` and `mark-resolved` for every other one. |

## @newheap/platform-ai-chat: proposals the application executes

The proposal cards of the runs view, the thread, the proposal views and the approval inbox show the new
status `awaiting-application` with the report deadline and a note that the application carries the
proposal out; an `execution-unknown` proposal the application executes offers "Hand to the application
again" next to "Mark as resolved" instead of "Run again". `AgentProposalStatus` and `ApprovalStatus` have
the value `awaiting-application`, `AgentProposal` the field `executionReportDueAt`, and
`AgentExecutionResolution` the value `hand-to-application`; the new texts have English and Dutch versions.
The mock API takes `executedByApplication`, `executionReportDueAt` and `executionResultCode` for the
proposals of its scenario and serves `POST approvals/{id}/resolve` as the server does.

| Breaking change | Required action |
|---|---|
| `AgentProposalStatus` and `ApprovalStatus` have the new value `awaiting-application`, and `AgentExecutionResolution` the value `hand-to-application`. | Let exhaustive switches and status maps of your own handle them. |
| The texts `nh-assistant.approval.status.awaiting-application`, `nh-assistant.agent-runs.proposal.status.awaiting-application`, `nh-assistant.agent-runs.proposal.hand-to-application`, `nh-assistant.agent-runs.proposal.unknown-application-hint`, `nh-assistant.agent-runs.proposal.awaiting-application-hint` and `nh-assistant.agent-runs.proposal.report-due` are new. | Add them to your application's translations when it overrides the library's texts (`translations: 'host'`). |

## NewHeap.Platform.AI.Common, NewHeap.Platform.AI.Chat, NewHeap.Platform.AI.Chat.AspNet, NewHeap.Platform.AI.Chat.SqlServer and NewHeap.Platform.AI.Chat.PostgreSql: instructions from the application's text assets

`INhAiTextAssetSource` in `NewHeap.Platform.AI.Common` supplies the application's own text assets by id
and version (`GetAsync`, `null` when it has none) and lists the ones administrators may choose
(`ListAsync`, manifests without content); `UseTextAssetSource<TSource>()` registers it with the
assistant. A data skill version (`NhAssistantDataSkillRequest.InstructionsAsset`) and an agent created in
the administration (`NhAssistantAgentRequest.InstructionsAsset`, `instructionsAsset` of `POST` and
`PUT admin/agents`) may then store an `NhAssistantInstructionAssetReference` (asset id, exact version,
SHA-256 of the content) instead of inline instructions; inline instructions stay possible, never next to
a reference. A save asks the source and refuses a missing asset (`instructionsAsset: not-found`) and
another hash (`instructionsAsset.hash: invalid`); every load of the run configuration asks again and
reports `no-asset-source`, `asset-missing`, `asset-changed`, `asset-invalid` or `asset-unavailable`: the
skill version is out of effect, and the agent starts no model turn
(`assistant-agent-instructions-unavailable`) while its approved proposals still execute. A new version
of an asset is used only once an administrator saves it as the next version. `GET admin/instruction-assets`
and `GET admin/instruction-assets/{id}/versions/{version}` list and read the texts for administrators;
the run catalog reports `InstructionAssets`, the skill and agent DTOs `InstructionsAsset`, and
`NhAssistantAdminAgentDto` `InstructionsProblem`. The audit names the asset as `InstructionsVersion` with
`InstructionsHash`, never the text. Without `UseTextAssetSource` nothing changes.

| Breaking change | Required action |
|---|---|
| The `InstructionAssets` migrations add the nullable columns `InstructionsAssetId` and `InstructionsAssetVersion` to `AssistantAgentSkillDefinition` and the column `InstructionsFromAsset` (default `false`) to `AssistantAgent` on SQL Server and PostgreSQL. | Apply the assistant migrations before the new version starts (`RunMigrations`, or your deployment's migration step). |
| `NhAssistantRunConfigurationProblems` has the new codes `no-asset-source`, `asset-missing`, `asset-changed`, `asset-invalid` and `asset-unavailable`, also for the kind `agent`. | Let code that maps run configuration problems handle values it does not know. |
| `NhAssistantErrorCodes` has the new code `assistant-agent-instructions-unavailable` (HTTP 409), and `NhAssistantAdminErrorCodes` the codes `assistant-instructions-asset-not-found` (HTTP 404) and `assistant-instructions-asset-unavailable` (HTTP 503). | Let code that maps assistant codes handle values it does not know. |
| `AdminAgentCreated`, `AdminAgentUpdated` and `AdminAgentReset` now carry `InstructionsVersion` (`<asset-id>@<version>`) and `InstructionsHash`, and `AdminAgentSkillDefinitionChanged` carries `InstructionsVersion` for a version that names an asset. | Let audit sinks that store every property expect values where these were `null`. |

## @newheap/platform-ai-chat: instructions from the application's text assets

When the run catalog reports `instructionAssets`, the skill form and the agent editor of
`@newheap/platform-ai-chat/admin` ask where the instructions come from: "Written here" or "The
application's text". With the application's text an administrator picks one of the listed texts, or
types its id and version when the application lists none, reads it read-only with its hash and saves
the reference; a newer version of the same text is pointed out and used only once saved. A saved
reference that no longer resolves shows its problem in the form, in the skill's card and, for an agent,
in the agent list. `NhAssistantAdminApiService.listInstructionAssets` and `getInstructionAsset`,
`InstructionAssetReference`, `InstructionAssetProblem`, `InstructionAssetSummary`, `InstructionAssetList`,
`InstructionAssetText`, `instructionsAsset` of `DataSkill`, `DataSkillRequest`, `DataSkillVersion` and
`AdminAgentInput`, `AdminAgent.instructionsProblem` and `RunCatalog.instructionAssets` complete the client.
The texts have English and Dutch versions. The mock API takes `admin.instructionAssets` of its scenario
and changes them with `NhAssistantMockBackend.setInstructionAsset` and `removeInstructionAsset`.

| Breaking change | Required action |
|---|---|
| The texts under `nh-assistant.admin.instruction-assets`, the problems `asset-*` and `no-asset-source` under `nh-assistant.admin.run-config.problems.code`, and the errors `assistant-instructions-asset-not-found`, `assistant-instructions-asset-unavailable` and `assistant-agent-instructions-unavailable` are new. | Add them to your application's translations when it overrides the library's texts (`translations: 'host'`). |

## NewHeap.Platform.AI.Chat, NewHeap.Platform.AI.Chat.AspNet, NewHeap.Platform.AI.Chat.SqlServer and NewHeap.Platform.AI.Chat.PostgreSql: trigger versions and candidates, and start states

Triggers defined as data have append-only versions, as data skills do. A change is the trigger's next
version, and `Apply` of `NhAssistantDataTriggerRequest` chooses how it takes effect: `live` (the default,
as a change took effect before) replaces the live version at once in the mode the trigger has; `shadow`
makes it the trigger's candidate, which fires next to the live version on its own definition, for the
trigger's own agent and always into the agent's shadow threads, with its own filters, dedupe, conflict,
debounce and daily limit, so it never refuses, queues or deduplicates the live version's work.
`PromoteTriggerCandidateAsync` and `DiscardTriggerCandidateAsync` (`POST
admin/agents/{id}/triggers/{triggerId}/candidate/promote` and `.../discard`) end the evaluation,
`CompareTriggerCandidateAsync` (`GET .../candidate/comparison`, `NhAssistantTriggerComparisonDto`) counts
both versions' runs, outcomes, usage and proposals per tool with the events both, only the live version
or only the candidate started a run for, and `GetTriggerVersionsAsync` (`GET .../versions`,
`NhAssistantDataTriggerVersionDto`) lists the history, which stays after the trigger is removed; a
trigger added again under a removed id continues its version numbers. `NhAssistantDataTriggerDto`
reports its `Candidate`, `CreatedAt` and `CreatedBy`, runs and `NhAssistantRunDto` record
`TriggerVersion` and `TriggerCandidate`, and `AgentRunEnded` carries `TriggerVersion` next to `Shadow`:
a candidate's runs are shadow runs of its version. Triggers saved before keep their current version as
the live one.

An agent created at runtime may start in shadow (`NhAssistantAgentRequest.StartsInShadow`,
`startsInShadow` of `POST admin/agents`): the shadow override is stored with its first row and audited as
`AdminAgentShadowChanged`, so it applies as soon as the agent has run settings, also when they and its
triggers are saved in the same request. A new consultation defined as data may start off
(`NhAssistantDataConsultationRequest.StartsOff`, or `StartOff()` on the ceiling of
`UseConsultationsAsData` for every new one): it is saved and checked but offers no tools until
`SetConsultationStateAsync` (`POST admin/agents/{id}/consultations/{subjectType}/state` with
`NhAssistantConsultationStateRequest`) switches it on; `NhAssistantDataConsultationDto.Off` and
`NhAssistantConsultationCeilingDto.StartsOff` report it. Both start states are opt-in: agents start
live and consultations on unless asked, and consultations saved before stay on.

| Breaking change | Required action |
|---|---|
| The `TriggerVersions` migrations change the primary key of `AssistantAgentTriggerDefinition` from `Id` to `(Id, Version)`, add its column `Status` (default `live`) with indexes on `(AgentId, Status)` and `(Id, Status)`, the column `Off` (default `false`) to `AssistantAgentConsultationDefinition`, the nullable column `TriggerVersion` and the column `TriggerCandidate` (default `false`) to `AssistantRun`, and the column `TriggerCandidate` (default `false`) to `AssistantQueuedInput` on SQL Server and PostgreSQL. Existing triggers become their current version, live. | Apply the assistant migrations before the new version starts (`RunMigrations`, or your deployment's migration step), and stop hosts of the previous version before administrators save trigger changes: those hosts read one row per trigger. |
| `INhAssistantAgentConfiguration` has the new members `GetTriggerVersionsAsync`, `PromoteTriggerCandidateAsync`, `DiscardTriggerCandidateAsync`, `CompareTriggerCandidateAsync` and `SetConsultationStateAsync`. | Implement them in a class of your own that implements the interface, or delegate to the registered one. |
| `NhAssistantRunConfigurationProblem.Kind` has the new value `trigger-candidate`, and `NhAssistantErrorCodes` the new code `assistant-trigger-candidate-not-found` (HTTP 404). | Let code that maps run configuration problems and assistant codes handle values it does not know. |
| `AgentRunEnded` carries the new property `TriggerVersion`; `AdminAgentTriggerDefinitionChanged` has the new result codes `candidate`, `promoted` and `discarded` and reports `created` for every trigger saved without an expected version, also one added again under a removed id; `AdminAgentConsultationDefinitionChanged` has the new result codes `on` and `off`. | Let audit sinks that store every property, or map result codes, expect the new values. |
| A removed data trigger keeps its versions as history instead of being deleted, and `RemoveTriggerAsync` discards its candidate. | None, unless the application counts the rows of `AssistantAgentTriggerDefinition`; then count rows with `Status` `live`. |

## @newheap/platform-ai-chat: trigger candidates and start states in the administration

The trigger form of `@newheap/platform-ai-chat/admin` asks how a change of a saved trigger takes effect:
"Apply at once" (the default) or "Evaluate in shadow next to the live version first" (`apply: 'shadow'`).
The run configuration then shows the trigger's candidate with what it changes against the live version,
its problem when it no longer fits the code, the comparison of both versions and their shared events,
promote and discard after a confirmation, and every trigger's history.
`NhAssistantAdminApiService.getDataTriggerVersions`, `promoteTriggerCandidate`, `discardTriggerCandidate`
and `compareTriggerCandidate`, `DataTriggerVersion`, `TriggerCandidateRequest`, `TriggerComparison`,
`DataTrigger.candidate`, `createdAt` and `createdBy`, `DataTriggerRequest.apply`, and `triggerVersion` and
`triggerCandidate` of a run complete the client. The agent editor offers "Start in shadow" for a new
agent that runs on its own (`startsInShadow`), and the consultation form "Start off" (`startsOff`, or a
note when the ceiling reports `startsOff`); the Consultations section marks a consultation that is off and
switches it on and off with `setDataConsultationState` (`DataConsultationState`,
`DataConsultationStateRequest`, `DataConsultation.off`). The texts have English and Dutch versions, and
the mock API keeps trigger versions, candidates, their history and the consultation state as the server
does.

| Breaking change | Required action |
|---|---|
| `RunConfigurationProblemKind` has the new value `trigger-candidate`. | Let code that switches over the kinds of run configuration problems handle it. |
| The texts `nh-assistant.admin.agent-editor.starts-in-shadow` and `starts-in-shadow-hint`, `nh-assistant.admin.run-config.problems.kind.trigger-candidate`, the texts under `nh-assistant.admin.run-config.triggers` for `saved-candidate`, `apply`, `apply-live`, `apply-shadow`, `versions` and `candidate`, the texts under `nh-assistant.admin.run-config.consultations` for `off-badge`, `off-explain`, `switch-on`, `switch-off`, `switched-on` and `switched-off` and their named variants, `form.starts-off`, `form.starts-off-hint` and `form.starts-off-ceiling`, and the error `assistant-trigger-candidate-not-found` are new. | Add them to your application's translations when it overrides the library's texts (`translations: 'host'`). |

## NewHeap.Platform.AI.Chat, NewHeap.Platform.AI.Chat.AspNet, NewHeap.Platform.AI.Chat.SqlServer and NewHeap.Platform.AI.Chat.PostgreSql: hand-offs and shadow variants as data

`UseHandOffsAsData(handOffs => handOffs.Allow(subjectType, rule => rule.To(...).From(...)).MaxPerSubjectPerDay(n))`
lets administrators connect agents with hand-offs without a deploy, only within this ceiling in code:
per subject type the agents that may accept them (`To`, `ToAgentsCreatedInAdministration`) and the agents
that may hand off (`From`, `FromAgentsCreatedInAdministration`), and the highest daily limit a hand-off may
set (at most and by default 100; a hand-off without one gets 5, or the ceiling's when lower); startup
applies the checks of `AcceptHandOffs` to the agents in code it names.
`INhAssistantDataHandOffs` (`GetAsync`, `SaveAsync`, `RemoveAsync`) and
`GET admin/agents/{id}/hand-offs`, `PUT` and `DELETE admin/agents/{id}/hand-offs/{subjectType}` read and
change them with `NhAssistantDataHandOffRequest` (sources, description, `NotifySource`, daily limit,
expected version), at most `NhAssistantDataHandOffLimits.MaxHandOffsPerAgent` (20) per agent and never one
the agent accepts in code. A saved hand-off works as one in code: the same generated tool, depth, scope,
daily limit and shadow rules, with `NhAssistantHandOffOptions.Source`, `DataVersion` and `IsData`. Every
load checks it again; one that no longer fits the ceiling or the code is out of effect as a whole with a
problem of the new kind `hand-off`, never used with less.

`UseShadowVariantsAsData(variants => variants.Of(liveAgentId).OfAgentsCreatedInAdministration().MaxPerAgent(n))`
lets administrators create shadow variants of the live agents it names (default 3, at most 10 per live
agent): `shadowOf` of `POST admin/agents` and `NhAssistantAgentRequest.ShadowOf` of `CreateAgentAsync`
create an agent in the administration that names its live agent once and never changes it. Once it has
run settings it receives the live agent's triggers, always in shadow, is never offered in the chat and is
paired by the evaluation and replays as a variant in code is. `GET admin/agents/{id}/shadow-variants`
lists the variants in code and as data (`NhAssistantShadowVariantsDto`); `NhAssistantAdminAgentDto.ShadowOf`
and `NhAssistantAgent.ShadowOfAgentId` name the live agent of a data variant. A variant that no longer fits
has its run settings out of effect with a problem and never runs. Without either call nothing changes.

| Breaking change | Required action |
|---|---|
| The `HandOffsAsData` migrations add the table `AssistantAgentHandOffDefinition` and the nullable column `AssistantAgent.ShadowOfAgentId` on SQL Server and PostgreSQL. | Apply the assistant migrations before the new version starts (`RunMigrations`, or your deployment's migration step). |
| `NhAssistantRunConfigurationProblems` has the new kind `hand-off` with the new codes `unknown-source` and `outside-ceiling`, and for the kind `agent` of a data shadow variant the codes `outside-ceiling`, `unknown-live-agent`, `live-agent-not-live` and `too-many-variants`. | Let code that maps run configuration problems handle kinds and values it does not know. |
| `NhAssistantErrorCodes` has the new codes `assistant-handoffs-not-allowed`, `assistant-handoff-in-code` and `assistant-shadow-variants-not-allowed` (HTTP 409) and `assistant-handoff-not-found` (HTTP 404). | Let code that maps assistant codes handle values it does not know. |
| `NhAssistantAuditEventKind` has the new value `AdminAgentHandOffDefinitionChanged` (80), and `NhAssistantAuditEvent` the property `LiveAgentId`, set on `AdminAgentCreated` for a data shadow variant. | Let audit sinks with an exhaustive switch, or that store every property, record or ignore them. |
| `DELETE admin/agents/{id}/run-settings` (`RemoveRunSettingsAsync`) also fails with `assistant-run-settings-in-use` while the agent accepts a hand-off defined as data. | Remove the agent's hand-offs first when it should lose its run settings. |

## @newheap/platform-ai-chat: hand-offs and shadow variants as data

Below the run configuration, `NhAssistantAdminAgentsComponent` of `@newheap/platform-ai-chat/admin` shows
an agent's hand-offs (`nh-assistant-agent-hand-offs`) and shadow variants
(`nh-assistant-agent-shadow-variants`). The hand-offs list those in code read only and those defined as
data with their version, sources, daily limit, generated tool and problem; the form adds and changes one
with only the kinds of object and sources the ceiling allows. The shadow variants list those in code and
as data, and "New shadow variant" creates one from the agent's definition and continues in its run
configuration. `NhAssistantAdminApiService.getAgentHandOffs`, `saveDataHandOff`, `removeDataHandOff` and
`getShadowVariants`, `AgentHandOffs`, `CodeHandOff`, `DataHandOff`, `DataHandOffRequest`, `HandOffCeiling`,
`HandOffAllowance`, `AgentShadowVariants`, `ShadowVariant` and `AdminAgentInput.shadowOf` complete the
client. The texts have English and Dutch versions. The mock API takes `admin.handOffsAsData`,
`admin.shadowVariantsAsData` and `codeHandOffs` of its agents and serves the new routes as the server does.

| Breaking change | Required action |
|---|---|
| `RunConfigurationProblemKind` has the new value `hand-off`. | Let exhaustive switches and maps of your own handle it. |
| The texts under `nh-assistant.admin.hand-offs` and `nh-assistant.admin.shadow-variants`, the kind `hand-off` and the codes `outside-ceiling`, `unknown-source`, `unknown-live-agent`, `live-agent-not-live` and `too-many-variants` under `nh-assistant.admin.run-config.problems`, and the errors `assistant-handoffs-not-allowed`, `assistant-handoff-in-code`, `assistant-handoff-not-found` and `assistant-shadow-variants-not-allowed` are new. | Add them to your application's translations when it overrides the library's texts (`translations: 'host'`). |
| The text `nh-assistant.errors.assistant-run-settings-in-use` now names hand-offs among what keeps an agent's run settings in use, and the mock API refuses to remove the run settings of an agent that accepts a hand-off defined as data. | Update the text in your application's translations when it overrides the library's texts. |

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: an ended run has its summary

An agent run stores its summary in the same write that ends it. Before, the end set the run's status and
`EndedAt` first and wrote the summary in a second update, so a client that polled a thread's runs, the
run history or `GET runs/{id}` in between read a run that had ended without its `Summary`, and did not
read it again because the run had ended; a person's turn that started at that moment missed the summary
in its `<agent-continuity>` block. Only the call that wins the end stores a summary, as before.

| Breaking change | Required action |
|---|---|
| None; a run that reads as ended always carries its `Summary`. | None. Remove waits or retries that poll an ended run until its summary appears. |

## NewHeap.Platform.AI.Chat, NewHeap.Platform.AI.Chat.AspNet, NewHeap.Platform.AI.Chat.SqlServer and NewHeap.Platform.AI.Chat.PostgreSql: agent packages

`INhAssistantAgentPackages` moves agents and their configuration as data between hosts as one versioned
package (`NhAssistantAgentPackage`, format `newheap.assistant.agent-package`, `formatVersion` 1): several
agents, each created in the administration whole or in code as an overlay of its data, without history,
candidates, runs, run state, shadow overrides, MCP servers, the technical account or secrets.
`ExportAsync` (`POST admin/agent-packages/export`, a JSON download) sets the package id, version and locks
and warns about references outside the selection in the header `X-NewHeap-Package-Warnings`, which a
host that serves the administration from another origin exposes in its CORS policy;
`NhAssistantAgentPackageFormat.Read` reads a package strictly. `PreviewImportAsync` (`POST admin/agent-packages/preview`) reports per part `created`,
`changed`, `unchanged`, `kept-local`, `left-behind` or `refused` with the field errors of its save, after
one check of the whole package against the host as it would be after the import; `ImportAsync`
(`POST admin/agent-packages/import`) writes it in one transaction with one change of the run
configuration, or nothing (`assistant-package-refused` with `parts: invalid`, the preview naming each
refused part), names the technical account per agent and makes changed skills and triggers
candidates with `Apply = "shadow"`. A package's `locks` keep named fields of its parts: every save path
refuses another value (`{field}: locked`), and the removal of a locked part or the deletion or reset of a
linked agent with `409 assistant-package-locked`, until `UnlinkAsync` (`POST
admin/agents/{id}/package/unlink`) takes the part out; the next upgrade then keeps it as `kept-local`
unless `Relink` names it. The administration's agent and part DTOs carry the link of a linked part
(`Package`). Without a package nothing changes.

| Breaking change | Required action |
|---|---|
| The `AgentPackages` migrations add the tables `AssistantAgentPackage` and `AssistantAgentPackageLink` on SQL Server and PostgreSQL. | Apply the assistant migrations before the new version starts (`RunMigrations`, or your deployment's migration step). |
| `NhAssistantErrorCodes` has the new codes `assistant-package-format-unsupported` (HTTP 400), `assistant-package-too-large` (HTTP 413), `assistant-package-refused` and `assistant-package-locked` (HTTP 409) and `assistant-package-not-linked` (HTTP 404), and `NhAssistantFieldErrors` the value `locked`. | Let code that maps assistant codes and field errors handle values it does not know. |
| `NhAssistantAuditEventKind` has the new values `AdminAgentPackageExported` (81), `AdminAgentPackageImported` (82) and `AdminAgentPackageUnlinked` (83), and `NhAssistantAuditEvent` the properties `PackageId`, `PackageVersion`, `PackageHash`, `PackagePart` and `PackageAgentCount`. | Let audit sinks with an exhaustive switch, or that store every property, record or ignore them. |
| Once a package with locks is imported, saves of agents, run settings, skills, triggers, consultations, hand-offs and metadata, candidate promotes, and the deletion and reset of a linked agent can fail with `locked` or `assistant-package-locked`. | Handle these refusals where application code saves agent configuration, and unlink a part before changing it. |
| `NhAssistantAdminAgentDto`, `NhAssistantAgentRunSettingsDto`, `NhAssistantDataSkillDto`, `NhAssistantDataTriggerDto`, `NhAssistantDataConsultationDto`, `NhAssistantDataHandOffDto` and `NhAssistantAgentMetadataDto` have the new property `package`, written only for a linked part. | Let clients that read these DTOs accept the new property. |
