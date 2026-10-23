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
application's own must return that tenant or none. Without the mode nothing changes.

| Breaking change | Required action |
|---|---|
| None. | None. |
