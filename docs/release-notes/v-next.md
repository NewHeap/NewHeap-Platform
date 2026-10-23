# v-next

## NewHeap.Platform.AI.AspNet.Common and NewHeap.Platform.AI.Chat.AspNet: consult and request tools only with a delegating initiator's rights

Security fix. In a headless run of an agent that acts with the rights of the account it answers to
(every agent created in the administration with run settings, or `ActWithAccountableRights()` in
code) and that does not delegate from its initiator, the agent gate allowed the generated
`assistant.consult.*` and `assistant.request.*` tools when that account passed their policies, and the
call then used the run's initiator as the asker. An event that named a person could so make the agent
consult another agent, or start its work, with that person's rights about any subject the person may
read, and put the answer in a thread that others read. The gate now allows these tools in an agent turn
only through the rights of the initiator the turn delegates from, never through the accountable
account's rights, a gateway read or an agent grant, so such a turn is not offered them; the tools also
refuse a call in an agent turn that its initiator did not delegate with
`assistant-consult-subject-unavailable`. A person's own chat, MCP consultations, runs that delegate from
their initiator, the routes of consultation proposals and requests by a person work as before.

| Breaking change | Required action |
|---|---|
| A headless run of an agent that acts with its account's rights and does not delegate from its initiator no longer consults or requests other agents. | Turn on delegation from the initiator (`delegateFromInitiator` in the run settings, `DelegateFromInitiator()` in code) for agents whose runs should consult with the rights of the person who started them. |

## NewHeap.Platform.AI.Chat, NewHeap.Platform.AI.Chat.AspNet, NewHeap.Platform.AI.Chat.SqlServer and NewHeap.Platform.AI.Chat.PostgreSql: consultations defined as data

`UseConsultationsAsData(data => data.AllowRoutes(...).AllowRequests().AllowMcp().MaxLimits(...))` lets
administrators define consultations at runtime within a ceiling in code: which agent may be consulted
about which object type, with which description and instructions, by whom, read-only or with proposals
by the routes the ceiling names, and within its limits. Agents created in the administration with run
settings get the application's ceiling; an agent in code only with `ConsultableAsData(...)`, whose ceiling
applies together with the application's. `INhAssistantAgentConfiguration.SaveConsultationAsync` and
`RemoveConsultationAsync` (`PUT` and `DELETE admin/agents/{id}/consultations/{subjectType}`) store one
versioned row per agent and subject type; the agent configuration lists code and data consultations and the
ceilings, and data that no longer fits after a deploy is reported as problems of kind `consultation`, out of
effect or with the parts that no longer fit dropped or capped. The generated tools of data consultations are
the catalog `assistant-consultations-data`, which every assistant host follows within seconds and whose
calls fail with `assistant-consult-agent-unavailable` once a consultation was removed or its contract
changed. Applications that do not call `UseConsultationsAsData` behave as before.

| Breaking change | Required action |
|---|---|
| The `AgentConsultationDefinitions` migrations add the table `AssistantAgentConsultationDefinition` and the nullable column `AssistantConsultation.ConsultationVersion` on SQL Server and PostgreSQL. | Apply the assistant migrations before the new version starts (`RunMigrations`, or your deployment's migration step). |
| `INhAssistantAgentConfiguration` has the new members `SaveConsultationAsync` and `RemoveConsultationAsync`. | Implement them in a class of your own that implements the interface; callers need nothing. |
| `NhAssistantAuditEventKind` has the new value `AdminAgentConsultationDefinitionChanged` (66), and `NhAssistantAuditEvent` the codes `ConsultationSource` and `ConsultationVersion`, which `AgentConsulted` now carries. | Let a business audit sink handle or ignore kinds it does not know. |
| With `UseConsultationsAsData`, the tools of data consultations come from the new catalog `assistant-consultations-data`, every assistant host runs the background refresh of the run configuration, and removing the run settings of an agent that has data consultations fails with `assistant-run-settings-in-use`. | Let an `INhAiToolDiscoveryPolicy` that lists exact tool ids allow the `assistant.consult.` and `assistant.request.` prefixes, and remove an agent's consultations before its run settings. |
| The catalog of consultations in code (`assistant-consultations`) reads the consultations registered in code only. | None. |

## @newheap/platform-ai-chat: consultations defined as data in the administration

The run configuration of an agent lists its consultations in a Consultations section: those in code
read only, and those defined as data, which administrators add, change and remove within the agent's
ceiling in code (`NhAssistantAdminApiService.saveDataConsultation` and `removeDataConsultation`), with
who may ask, the routes of its proposals, its limits, requests, MCP and the classification of its
answers. Each row names the agents that ask it, with `openAgent` to open their editors, and its problems;
the route switches show in the section for every agent with consultations, and the flow's when step
names them. `AgentRunConfiguration` carries `codeConsultations`, `dataConsultations` and
`consultationCeiling`, `RunCatalog` the application's `consultationCeiling`; the codes
`assistant-consultation-in-code`, `assistant-consultation-not-found` and
`assistant-consultations-not-allowed` and the field error `not-allowed` have English and Dutch texts. The
mock API plays consultations as data with `consultationsAsData`, `consultableAsData` and
`dataConsultations`.

| Breaking change | Required action |
|---|---|
| `RunConfigurationProblemKind` also allows `'consultation'`. | Add the value to an exhaustive `switch` over the kinds. |
| The route switches of an agent's consultations show inside the Consultations section for every agent with consultations, also one created in the administration, and the texts `code`, `not-allowed`, `not-allowed-hint`, `action.code`, `explain-change.code-on` and `explain-change.code-off` under `nh-assistant.admin.agents.consultation-routes` speak of the agent's consultations instead of its code. | Update your application's overrides of these translation keys. |

## NewHeap.Platform.AI.Common, NewHeap.Platform.AI.Mcp, NewHeap.Platform.AI.AgentFramework and NewHeap.Platform.AI.AspNet.Common: tool catalogs that change at runtime

`INhAiChangingToolCatalog` is an attested catalog whose descriptors change while the application runs, in
immutable views (`Current`) that are attested on their own. Discovery calls its `RefreshAsync` first, the
agent framework adapter and the MCP export read descriptors and functions from one view
(`NhAiToolCatalogViews.Of`), and the MCP export leaves a view that fails its attestation, or a tool that
would take an export name already taken, out instead of failing. When such a catalog may reach MCP
clients, `WithNewHeapPlatformAITools` signals `notifications/tools/list_changed` when it changes. Over
Streamable HTTP only a stateful server (`Stateless = false`, which needs session affinity across
instances) declares `listChanged` and tells its sessions, on their standalone GET stream; a stateless
server, the recommended setup and the SDK's default, declares no `listChanged` and its
`subscriptions/listen` grants no tool list changes, so its clients see a change on their next
`tools/list`. A call of a tool from a stale list fails with `ai-tool-not-authorized` and runs nothing.

| Breaking change | Required action |
|---|---|
| With a catalog that may reach MCP clients registered as `INhAiChangingToolCatalog`, `WithNewHeapPlatformAITools` replaces `McpServerOptions.ToolCollection` with a collection that holds the same tools and signals changes, and a stateful server declares `listChanged` for tools. | None; tools added to the options' collection after the server options were built are not copied. |
| The MCP startup check skips the export names of catalogs that change at runtime; their views are checked when they are exported. | None. |

## NewHeap.Platform.AI.AspNet.Common and NewHeap.Platform.AI.Chat.AspNet: the request's person by the platform's actor id

The platform recognizes the person who sent a request by the actor id the registered
`INhAiAuthenticatedInvocationContextResolver` resolves for it, the id the assistant records as a
conversation's owner, a run's initiator and a consultation's asker, instead of the `NameIdentifier`
claim. The ASP.NET context contributor adds the request's correlation id, active division and
capabilities to that person's contexts, a request turn of an agent reuses the initiator's live request
principal, and a consultation in the asker's own request reuses the asker's, also when the
application's actor ids differ from the claim, such as an issuer projection (`oidc:` ids) or an
application with its own identity model whose resolver returns its own actor ids. Before, such a
consulted agent got no tools. NewHeap Identity hosts resolve the claim and behave as before.

| Breaking change | Required action |
|---|---|
| A context built in a request for the person whose actor id is not the `NameIdentifier` claim (issuer projections, or an application's own resolver) now gets the request's correlation id, active division scope and division capabilities. | Review the `AddActiveDivisionScope` and `AddCapabilityGrant` policies of such hosts; NewHeap Identity hosts need nothing. |
| An application's own `INhAiAuthenticatedInvocationContextResolver` also runs, at most once per request, when the platform checks whether a request is a person's own. | Keep the resolver free of side effects and return the same actor id for a request every time. |

## NewHeap.Platform.AI.Chat: what rules learn about a proposal that is not needed, and what the model reads after a hand-over

`NhAssistantRuleProposal.NotNeededKind` (`NhAiStaleKind`) and `Explanations` (culture name to text,
the run's culture first) tell rules why a proposal-time guard found a proposal not needed, in every
supported UI culture, next to `Explanation` in the run's culture. After `NhAssistantRuleResult.HandOver`
the briefing's first section also lists what became of each proposal the rules asked for in the run
(tool, object and outcome) and the recent rejections and invalidations they were given (tool, date and
kind), at most 20 and 10 lines and within the briefing's bound; people's feedback stays in the agent
continuity block.

| Breaking change | Required action |
|---|---|
| The rules' section of a hand-over briefing is longer, up to 6,000 characters, and the application's own briefing sections share `MaxBriefingCharacters` with it. | Raise `NhAssistantLimits.MaxBriefingCharacters` when the application's sections are left out after a hand-over. |

## NewHeap.Platform.AI.Chat.AspNet: what releasing the kill switch does for a stopped agent

`NhAssistantAgentState.OffBeforeStop` and `NhAssistantAdminAgentDto.OffBeforeStop` (`offBeforeStop`)
are `true` only while an agent is stopped and was off when it stopped: releasing the kill switch then
switches it back off, while an explicit resume switches it on.

## @newheap/platform-ai-chat: the board's division and what releasing the kill switch does

`AdminAgent.offBeforeStop` and `AgentState.offBeforeStop` carry the server's field, and the agents
administration says for a stopped agent whether releasing the kill switch switches it back off or makes
it work again, in English and Dutch (`nh-assistant.admin.agents.state.stopped-was-off-hint` and
`stopped-was-on-hint`). The mock admin API serves `offBeforeStop` as the server does.

| Breaking change | Required action |
|---|---|
| The agent proposals that `nh-assistant-agent-board` shows on a board with `divisionId` are filtered on that division. | None; a board without `divisionId` shows the proposals of every division as before. |

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: proposals of consultations without a rebuilt principal

`AllowConsultation(..., consult => consult.Proposals(p => p.AskerDecides().AtSubject(rights).Policy(id)))`
chooses who decides what a consulted agent proposes. With `AskerDecides()` the person who asked decides
it right away in their own chat and it executes in their own request with their own rights; with
`AtSubject(NhAssistantConsultationRights.Agent)` it waits for the subject's approvers other than the
asker and executes with the agent's own rights. Neither rebuilds a person's principal outside that
person's request. `Proposals(bool, role)` keeps today's route (`AtSubject(Asker)`), and no existing
registration changes how its proposals are decided. Administrators switch the routes per agent within
what code allows (`INhAssistantAgentControl.SetConsultationRoutesAsync`,
`POST admin/agents/{id}/consultation-routes`), stored in the new `AgentConsultationRoutes` migrations.

| Breaking change | Required action |
|---|---|
| The maintenance job now expires and escalates every pending proposal of a consultation, today's route included; before, such a proposal stayed pending after its expiry until someone tried to decide it. After the upgrade, consultation proposals past their expiry close as `expired` on the next round, and policies with `OnExpiry(Escalate)` escalate them. | Check the expiry and escalation of the approval policies your consultations use; nothing to do when they should expire. |
| The `AgentConsultationRoutes` migrations add six nullable columns to `AssistantAgent` on SQL Server and PostgreSQL. | Apply the assistant migrations before the new version starts (`RunMigrations`, or your deployment's migration step). |
| `INhAssistantAgentControl` has the new member `SetConsultationRoutesAsync`. | Implement it in a class of your own that implements the interface; callers need nothing. |
| `NhAssistantAgentNotificationKind` has the new value `ConsultationProposalDecided`, sent only to the asker of a consultation proposal that waited at the subject, today's route included, when it is rejected, expires, closes or its execution ends. Askers of today's consultation proposals now get this notification through the application's agent notification channels. | Let a custom notification policy or channel handle it or ignore kinds it does not know; filter it out in a policy when askers should not hear of it. |
| The generated consult tool's output schema gains the optional `decision` and `approvalIds`; its version and export name stay, its contract and attestation hash change. | Refresh pinned MCP tool attestations of consult tools. |

## @newheap/platform-ai-chat: proposals of consultations

The approval card shows a consultation's proposal that the person decides in their own chat with the
consulted agent, the subject and its answer, and offers Leave for approvers when a route at the subject
applies; proposal views show who asked and the route; the agent thread view reads each pending
proposal's live decision right (`lookupProposals`), as the run result and the subject's work do, so the
person who asked sees the four-eyes reason instead of Approve and Reject, also as an approver; the run
configuration of agents in code switches the consultation routes; the mock API serves both routes.
Texts are in English and Dutch.

| Breaking change | Required action |
|---|---|
| `ApprovalDecision` also allows `'leave'` and `NhAssistantApprovalWaitingFor` also allows `'asker'`. | Add the new values to an exhaustive `switch` over these types. |

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: application messages and upload types

`INhAssistantConversationMessages.PostAsync(conversationId, NhAssistantApplicationMessage)` is the
supported way for an application to post its own message into a chat conversation or an agent thread,
for example when a long-running export finished: Markdown text within `MaxMessageChars`, an optional
title, sender name and link (a path within the application or an `https` address), `Notify` for a
content-free Web Push to the conversation's people (never from a shadow thread), an optional
`IdempotencyKey` and `ReadableBy` for a conversation a client named. The message gets the new role
`application` and the next sequence number, arrives live, is audited as `ApplicationMessagePosted`
without content, and a later turn gives it to the model as a user message marked `[Application]` whose
`application-message` block says it is information, never instructions. There is no HTTP endpoint for
clients. An upload without a media type, with a generic one (`application/octet-stream`) or with a type
devices send for its extension (such as `application/vnd.ms-excel` for `.csv`) now takes the type its
extension names when the application accepts it; the content check and the scanner still run, and a
specific type the application does not accept stays refused.

| Breaking change | Required action |
|---|---|
| Conversations can hold messages with role `application` (`NhAssistantMessageRoles.Application`, author kind `application`), in snapshots and in the live `message.created` event, with the new `NhAssistantMessageDto.Application`. | Render or skip the role in clients of your own that switch over message roles; the `nh-assistant-thread` component shows it. |
| `NhAssistantAuditEventKind` has the new value `ApplicationMessagePosted` (70). | Let an audit sink with an exhaustive switch record or ignore it. |
| Uploads that were refused for a missing or generic media type are accepted when their extension names an accepted type, such as a `.csv` sent as `application/vnd.ms-excel` when `text/csv` is accepted; an application that accepts both types now receives such a `.csv` as `text/csv` instead of `application/vnd.ms-excel`, and an `.xml` sent as `text/xml` as `application/xml` when it accepts that. | Handle `text/csv` (and `application/xml`) where the application processed those uploads as `application/vnd.ms-excel` (or `text/xml`); check the content of application-specific types in `StoreUploadAsync` or the scanner as before. |
| `NhAssistantUploadOptions.MapExtension(extension, mediaType)` maps an application's own extension, such as `.dwg` to `application/acad`, for uploads without a usable type; a mapping counts only while the type is accepted and takes precedence over the platform's table. `NhAssistantAgentUploadsDto.MappedExtensions` (`NhAssistantUploadExtensionDto`) gives the mappings of accepted types to clients. | None; call `MapExtension` in `ConfigureUploads` for extensions the platform does not know, and let clients of your own derive with `mappedExtensions` as the server does. |
| An upload whose type resolves to text (`text/*`, `application/json`, `application/xml` or a `+json`/`+xml` type) is refused with `assistant-file-type-not-accepted` when its content starts as an OLE2 compound file, a ZIP package, a PDF, PNG, JPEG or GIF, such as an `.xls` workbook renamed to `.csv`; a `.pptx` must now start as a ZIP package. UTF-16 text passes. | Upload such files with their real extension and type; accept the binary type when the application wants it. |

## @newheap/platform-ai-chat: application messages, upload types and the grouped inbox's description

`nh-assistant-thread` shows a message the application posted (`role: 'application'`) as the
application's, in chats and agent threads: a card with the sender, an "Application" tag, the title,
the Markdown text and a link that opens a route through the router or an `https` address and nothing
else (`Message.application`, `ApplicationMessageDetails`, `ApplicationMessageLink`). The composer gives
a file without a usable type the accepted type of its extension by the server's rule, and its file
picker offers the extensions of every accepted type it knows, so `.csv` files are listed for
`text/csv`. With `grouped`, `nh-assistant-agent-inbox` describes the grouped order
(`nh-assistant.agent-runs.inbox.description-grouped`). The mock API posts application messages with
`NhAssistantMockBackend.postApplicationMessage` and derives upload types as the server does. Texts
are in English and Dutch.

| Breaking change | Required action |
|---|---|
| `Message.role` also allows `'application'` and `MessageAuthorKind` also allows `'application'`. | Add the new values to an exhaustive `switch` over these types. |
| A tab that streams a turn now shows the live `message.created` of the application's and other people's messages instead of ignoring them until the conversation loads again, and `applyNhAssistantLiveEvent` puts a message where its `sequence` puts it, after the answer a turn is still writing, instead of last. | Nothing for `NhAssistantStore` and the panel; a custom view that applies live events itself does so with `applyNhAssistantLiveEvent` while it streams, too, and leaves out the turn's own events and message. |
| `AgentUploads.mappedExtensions` (`AgentUploadExtension`) carries the extensions the application mapped on the server; the composer derives a file's type with them before the platform's table, also `text/xml` for `.xml` as `application/xml`, and its file picker offers them. The mock API derives alike and refuses text whose content starts as a binary format, as the server does. | None; give an agent of a mock scenario `mappedExtensions` when the server maps extensions. |
