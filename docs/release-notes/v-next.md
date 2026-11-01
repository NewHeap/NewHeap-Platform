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

## @newheap/platform-ai-chat: chats with an agent offered only for chats about a subject

- An agent with `RequireChatSubject()` now works from the panel. `NhAssistantStore` read the status before
  every message and chose among its agents, which leave such an agent out, so the first message went to another
  agent or was refused with `assistant-agent-does-not-work-on`. The store now chooses among `chatAgents()`, keeps
  the agent of the open chat while it knows that agent, and chooses nothing while the agents of a subject load.
- For a subject without agents, the chosen agent stays and the server refuses it, so the person sees why instead
  of the message being dropped.
- The mock API has `requireChatSubject` on `NhAssistantMockAdminAgent`. `GET agents?subjectType=&subjectId=` of the
  mock now lists only the agents that work on the subject or chat about it (`chatsAbout`, for a chat-only agent),
  and a new chat answers `assistant-subject-required` and `assistant-agent-does-not-work-on`, as the server does.

## NewHeap.Platform.AI.Chat: chats about an object with a chat-only agent

- `ChatsAbout(types)` on `NhAssistantAgentRunBuilder` names the object types people may chat with a `ChatOnly()`
  agent about. `GET agents?subjectType=&subjectId=` and `ListAgentsAsync(person, subject)` list the agent for an
  object of such a type, and a new chat may name one, with the object's access resolver on creation and on every
  turn, read and invitation, as for any chat about a subject.
- The agent keeps what a chat-only agent has, such as skills in chat and structured questions. `WorksOn` stays
  nothing, so every start, trigger, hand-off and consultation stays refused, no object's page shows it and it joins
  no board or run maintenance.
- `RequireChatSubject()` is now valid next to `ChatsAbout`, and stays refused for a `ChatOnly()` without it.
- Startup refuses `ChatsAbout` without `ChatOnly()` and a type without a subject access resolver.
- `NhAssistantAgentRunOptions.ChatsAbout` and `NhAssistantAgent.ChatsAbout` hold the types. No migration is needed.
