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
