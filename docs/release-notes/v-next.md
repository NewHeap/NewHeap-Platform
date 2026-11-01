# v-next

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
