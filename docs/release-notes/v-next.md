# v-next

## NewHeap.Platform.AI.Common, NewHeap.Platform.AI.AspNet.Common, NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: telling a person's chat from agent work

`NhAiInvocationContext.IsAgentWork` says whether a tool call is an agent's own work rather than a person's
chat, so a rule about runs no longer blocks the application's own chat. It is true in a run, a rule run, a
shadow run, a consultation and when a proposal of a run or a consultation executes. It is false in a person's
chat, the chat's follow-up turn after an execution report, a person's own turn in an agent's thread and a
proposal the application records for a person. A step of a composition or a script counts as the turn it runs
in. The agent invocation gate copies it from `NhAiAgentInvocationScope.IsAgentWork`, true by default, and
`INhAiBackgroundOperationRunAdapter` sets it for a durable run.

Nothing changes for existing checks: `ActorKind` stays `Agent` in a person's chat, because the agent is the
actor there, `RunId` stays the turn's id and `AgentRunId` the run's, and no proposal hash, contract hash or
usage record reads the new property. An application that combined `AgentRunId` with
`NhAiAgentInvocationScopes.Current`, or that put a rule for runs on `ActorKind`, should read `IsAgentWork`
instead. SPM-387 shows such a rule, and `nh-ai-agent-runs` says which turns count.
