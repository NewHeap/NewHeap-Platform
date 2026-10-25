# NewHeap.Platform.AI.AgentFramework

Optional Microsoft Agent Framework integration for generated NewHeap AI tool
catalogs. The adapter resolves a named provider-neutral model profile, performs
actor-specific `Agent` discovery, filters tools by the declared autonomy and
allow-list, and creates a `ChatClientAgent`. Tool execution still runs through
the shared NewHeap authorization, capability, approval, budget, idempotency,
concurrency, and verification pipeline.

`NhAiAgentCreateRequest.ChatHistoryProviderFactory` and `AIContextProvidersFactory` give the agent
an Agent Framework `ChatHistoryProvider` and `AIContextProvider`s of the application's choice, such as
an `InMemoryChatHistoryProvider` or memory and compaction. Every model call still crosses the governed
client, and a context provider that adds a tool fails the run before the model is called. A failed
governed call throws `NhAiAgentModelCallException`, whose `Code` keeps the failure code, such as
`ai-model-timeout`; the caller's own cancellation still throws `OperationCanceledException`.

The package does not own model-provider credentials, prompt persistence,
conversation persistence, approval storage, or domain state.

`INhAiAgentFrameworkWorkflowCheckpointAdapter` binds the official workflow
`CheckpointInfo` session and checkpoint IDs to a versioned, content-free
NewHeap checkpoint reference. Applications still own a trusted Agent Framework
checkpoint manager/store and the serialized workflow state. Resume must match
the adapter, workflow version, session, checkpoint ID and state hash exactly;
changing stable workflow or executor identities starts a new lineage.
