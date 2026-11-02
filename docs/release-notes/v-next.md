# v-next

## @newheap/platform-ai-chat: a new chat after a chat about a subject

- After a chat with an agent that only a chat about a subject offers, "+" (`NhAssistantStore.startNewConversation`)
  kept that agent chosen. The picker then showed the first agent of the list, and the store chose the default
  agent only when the first message went out, so the message reached another agent than the picker showed.
  `startNewConversation` now chooses among the agents of a new chat right away, unless they are still loading.

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: decision times and the view per kind of action (FB82)

- `GET admin/agents/{id}/feedback/summary` rows carry `decisionTime` (`NhAssistantDecisionTimeDto`): the median and
  the 90th percentile of the time from a proposal to a person's approval or rejection, in whole seconds, from at most
  the newest 1,000 decisions per row (`sampled` says when there were more). Expired, withdrawn and invalidated
  proposals and decisions of agents or of the platform do not count. Computed with provider-neutral LINQ and a
  bounded load on SQL Server and PostgreSQL; no migration.
- `GET admin/proposal-kinds` (`NhAssistantProposalKindsDto`) is the administration's view per kind of action over all
  agents: per tool the counts, approval and rejection rates, decision times, the newest rejection and the name the
  proposal cards show, and per agent a `NhAssistantRoutineCandidateDto` (`agentId`, `toolId`, the numbers,
  `candidate`, `unmetThresholds`) by the thresholds of `NhAssistantRoutineCandidateOptions`
  (`NewHeap:AI:Assistant:RoutineCandidates`, `NhAssistantBuilder.ConfigureRoutineCandidates`): at least 50
  decisions, at least 98% approved, no rejection in the last 7 days of the period and none stale or failed after
  approval by default. Filters `agentId` and `toolId`; behind the admin policy. A candidate is a suggestion only:
  nothing about deciding changes.

## @newheap/platform-ai-chat: decision times and the Kinds of action section (FB82)

- The administration (`nh-assistant-admin`) has a Kinds of action section (`NhAssistantAdminTab` `proposal-kinds`,
  `NhAssistantAdminProposalKindsComponent`) between Agents and Tools & connections: per kind of action over all agents
  the name the cards show, the decisions, the rejection rate and the decision times, and per agent whether the
  numbers make the kind a candidate for running without approval, with the thresholds explained and a note that the
  candidates are suggestions for administrators only. Agent rows are `NhAssistantRoutineCandidateRowComponent`;
  `candidateActions` adds a column with the host's own action per candidate.
- `NhAssistantAdminApiService.getProposalKinds` with `ProposalKinds`, `RoutineCandidate` and the related models;
  `AgentFeedbackSummaryRow.decisionTime`, which the feedback report shows as two columns.
- The mock API of `@newheap/platform-ai-chat/testing` answers `GET admin/proposal-kinds` with an example: a status
  change that is a candidate and a task that misses three thresholds.

| Breaking change | Required action |
|---|---|
| `NhAssistantAdminTab` has the value `proposal-kinds`. | A host that maps its URL to the section or switches over the tab accepts `proposal-kinds`, as `sampleAssistantAdminLocation` does with `?section=proposal-kinds`. |

## NewHeap.Platform.AI.Common, NewHeap.Platform.AI.Generators, NewHeap.Platform.AI.Chat, NewHeap.Platform.AI.Chat.SqlServer, NewHeap.Platform.AI.Chat.PostgreSql and NewHeap.Platform.AI.Chat.AspNet: running a kind of action without approval (FB82)

- The one exception to "an agent's effect waits for a person", with two keys and shadow first. The tool's key is in
  code: `NhAiToolAttribute.AllowWithoutApproval` (and `NhAiToolDescriptor.AllowWithoutApproval`) says the tool's change
  is reversible and of low risk. The generator refuses it with `NHAI019` without `Agent` exposure and a named policy,
  for any effect but `IdempotentMutation` or `Mutation` and for an issuer or consumer-authoritative approval;
  `NhAiWithoutApproval.Validate` and `NhAiToolCatalogAttestation.Validate` refuse the same for runtime catalogs. The key
  enters the contract hash.
- The administrator's key is data: `INhAssistantWithoutApprovalSwitches`, `GET admin/agents/{id}/without-approval`,
  `PUT admin/agents/{id}/tools/{toolId}/without-approval` (`{ mode, expectedVersion, reason }`) and
  `GET .../without-approval/versions`, behind the admin policy. A switch goes from `off` to `shadow`, and only from
  `shadow` to `live` (`409 assistant-without-approval-shadow-first`); a tool without the key, outside the agent's tools
  or of an agent without work of its own answers `409 assistant-without-approval-not-allowed`. Each change is the next
  version of `AssistantAgentToolSwitch` with who switched, why and the contract hash it applies to, audited as
  `AdminAgentToolWithoutApprovalChanged`; a changed contract reads as off until it is switched again.
- The governed invoker asks `INhAiWithoutApprovalSwitches` (default: always off) only for an agent's own run outside
  scopes that require approval for every effect, for an approval of the default effect policy and without approval
  evidence. In shadow the call still becomes its proposal, which records `AssistantApproval.WithoutApprovalVersion`
  and the audit `AgentProposalWouldRunWithoutApproval`. Live, the call runs without a proposal once the approval
  policy selector, open and rejected proposals and every guard stage agree, with every other rule intact; the audit
  record has the approval code `without-approval` with `WithoutApprovalMode` and `WithoutApprovalSwitchVersion`, the
  call's row `WithoutApprovalVersion` and the business audit `AgentToolRanWithoutApproval`. A person's chat, a
  consultation, a rule turn, a shadow run and a call for another object always ask; switching off applies to the next
  call. `GET admin/tools` reports `allowWithoutApproval` per tool.
- The `AgentToolWithoutApproval` migrations for SQL Server and PostgreSQL add the table `AssistantAgentToolSwitch` and
  the column `WithoutApprovalVersion` to `AssistantApproval` and `AssistantToolInvocation`.

| Breaking change | Required action |
|---|---|
| A tool that sets `AllowWithoutApproval` gets another contract hash, so its open proposals expire as for any contract change. | Set it in a deployment where its open proposals may expire, or decide them first. |
| The assistant's startup check refuses another registered `INhAiWithoutApprovalSwitches`. | Do not replace it; switch tools per agent through `INhAssistantWithoutApprovalSwitches`. |
| The assistant schema has the `AgentToolWithoutApproval` migration. | Apply the assistant migrations for your provider, or keep `RunMigrations` on. |

## @newheap/platform-ai-chat: running a kind of action without approval (FB82)

- An agent's Rollout lists the kinds of action it may run without approval: the tools whose code allows it, each with
  its switch ("Try in shadow", then "Run without approval", and "Switch off"), who switched it last, the note and
  whether the tool changed since. The Kinds of action section shows the same switch in each agent's row through its
  `candidateActions`; going live from a kind that is no candidate names the thresholds it misses first.
  `NhAssistantWithoutApprovalActionComponent` is exported for hosts that render that section themselves.
- `NhAssistantAdminApiService.getAgentWithoutApproval`, `setAgentWithoutApproval` and
  `getAgentWithoutApprovalVersions` with `AgentWithoutApproval`, `WithoutApprovalSwitch`, `WithoutApprovalRequest`,
  `WithoutApprovalVersion` and `WithoutApprovalMode`; `ToolCatalogEntry.allowWithoutApproval`; English and Dutch
  texts for `assistant-without-approval-not-allowed` and `assistant-without-approval-shadow-first`.
- The mock API of `@newheap/platform-ai-chat/testing` keeps the switches with their versions and refuses as the server
  does. The notice of the Kinds of action section now says that a candidate changes nothing by itself.
