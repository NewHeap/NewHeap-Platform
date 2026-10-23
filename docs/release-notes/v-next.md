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
