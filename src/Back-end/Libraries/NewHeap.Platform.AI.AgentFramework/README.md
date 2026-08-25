# NewHeap.Platform.AI.AgentFramework

Optional Microsoft Agent Framework integration for generated NewHeap AI tool
catalogs. The adapter resolves a named provider-neutral model profile, performs
actor-specific `Agent` discovery, filters tools by the declared autonomy and
allow-list, and creates a `ChatClientAgent`. Tool execution still runs through
the shared NewHeap authorization, capability, approval, budget, idempotency,
concurrency, and verification pipeline.

The package does not own model-provider credentials, prompt persistence,
conversation persistence, approval storage, or domain state.

`INhAiAgentFrameworkWorkflowCheckpointAdapter` binds the official workflow
`CheckpointInfo` session and checkpoint IDs to a versioned, content-free
NewHeap checkpoint reference. Applications still own a trusted Agent Framework
checkpoint manager/store and the serialized workflow state. Resume must match
the adapter, workflow version, session, checkpoint ID and state hash exactly;
changing stable workflow or executor identities starts a new lineage.
