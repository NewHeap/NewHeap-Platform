# v-next

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
| Uploads that were refused for a missing or generic media type are accepted when their extension names an accepted type, such as a `.csv` sent as `application/vnd.ms-excel` when `text/csv` is accepted. | None; check the content of application-specific types in `StoreUploadAsync` or the scanner as before. |

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
