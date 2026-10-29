# v-next

## NewHeap.Platform.AI.AgentFramework: a model service that keeps the conversation next to a history provider

`NhAiAgentCreateRequest` takes three init-only switches that set Agent Framework's
`ChatClientAgentOptions` switches of the same names: `ThrowOnChatHistoryProviderConflict`,
`WarnOnChatHistoryProviderConflict` and `ClearOnChatHistoryProviderConflict`, all `true` by default as
in Agent Framework. They matter when the model service keeps the conversation itself, as the OpenAI
Responses API does, while the agent has a history provider from `ChatHistoryProviderFactory`: by
default the run still fails after the model call with "Only ConversationId or ChatHistoryProvider".
History the service keeps lives at the model provider, outside the application's retention and
forgetting, so switch the error off only in a deployment whose conversation data may live there. The
session then follows the service's conversation id, and the provider is not used for it after that
first answer. `WarnOnChatHistoryProviderConflict` logs through the logger factory the model client
offers, and `ClearOnChatHistoryProviderConflict` removes the provider from the agent instance.

| Breaking change | Required action |
|---|---|
| None; the switches keep Agent Framework's defaults, so an agent without them behaves as before. | None. Set `ThrowOnChatHistoryProviderConflict = false` only where conversation data may live at the model provider. |
