# v-next

## NewHeap.Platform.AI.Common, NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: a person's chat continues after an execution report

`ExecuteInApplication(toolId, reportWithin, execution => execution.AlsoInline().ContinueChatAfterReport())`
(`NhAssistantApplicationExecutionBuilder.ContinueChatAfterReport`) continues a person's chat once the
application reported the execution of a proposal approved there, so the person no longer picks the work
up again after a slow execution. After a report of any outcome the assistant starts one turn in that chat
that nobody typed, after a two-second debounce on the application's clock, so reports that arrive
together reach one turn. The turn runs as the person whose turn made the proposal, never as a decider,
the chat's owner or another participant: `INhAiPrincipalFactory` rebuilds them in the active division of
that turn, the context resolver resolves them, and they must still be in the chat and pass the
assistant's access policy and the agent's policy. It can only propose: every tool effect other than a read
needs that person's approval in the chat, and a proposal waits for their decision like any other. The
report stays the application's information for the model. The turn's tools and audit events carry the
channel `execution-report` (`NhAssistantDecisionChannels.ExecutionReport`), and the audit records
`ChatFollowUpStarted`, or `ChatFollowUpSkipped` with the reason when the chat runs a turn or waits for a
decision or an answer, has a newer message than the report (`NhAssistantApplicationExecutionCodes.ChatAnswered`)
or the person fails a check; the report then waits for the person's next message. A refused or failed
hand-off and a passed report deadline start no turn. The debounce lives in the process. Off by default;
a declaration with the switch and without `AlsoInline` is refused.

| Breaking change | Required action |
|---|---|
| `NhAssistantAuditEventKind` has the new values `ChatFollowUpStarted` (121) and `ChatFollowUpSkipped` (122), recorded only for a tool declared with `ContinueChatAfterReport`. | Let audit sinks with an exhaustive switch record or ignore them. |
