---
id: nh-ai-agent-framework
title: "Create policy-bound Agent Framework agents"
area: backend
reference: ai-agent-framework
summary: "Adapt named NewHeap model profiles and generated tool catalogs to Microsoft Agent Framework without moving identity, authorization, approval, or provider ownership into the framework layer."
sample-cases: ["SPM-225", "SPM-236"]
public-symbols: ["INhAiAgentFrameworkAdapter", "INhAiAgentFrameworkWorkflowCheckpointAdapter", "NhAiAgentCreateRequest", "NhAiAgentDescriptor", "NhAiAgentFrameworkServiceCollectionExtensions", "NhAiAgentInstance", "NhAiAutonomyLevel"]
skills: ["newheap-backend-development", "newheap-background-processing"]
providers: ["provider-neutral"]
risk: high
---
## Preferred approach

Reference `NewHeap.Platform.AI.AgentFramework` only in hosts that need Microsoft
Agent Framework. Register it after AI Common composition. Keep provider clients
consumer-owned behind named `IChatClient` profiles; no provider package belongs
in AI Common or the generic adapter.

Define a stable `NhAiAgentDescriptor` with lowercase ID, version, accountable
model profile, required model capabilities, explicit tool selectors, autonomy
ceiling, budgets, and evaluation baseline. Create the invocation context as an
`Agent` actor with a separate accountable owner, matching agent version, narrow
execution scope and short-lived capabilities. Agent identity and authority are
host metadata, never prompt text.

The adapter resolves the named model profile and repeats actor-specific tool
discovery for the `Agent` exposure boundary. It intersects that result with the
descriptor's allow-list and autonomy ceiling before constructing a
`ChatClientAgent`. Agent creation returns `TaskResult<NhAiAgentInstance>` for
expected profile/policy failures. Model calls run through `INhAiChatExecutor`
with the strict intersection of descriptor and run budgets, the request data
classification, deadline, maximum estimated-cost reservation, usage sinks, and
content-free telemetry. All functions
must be descriptor-bound `INhAiGovernedAIFunction` instances from a
`SharedInvoker` catalog, so
authorization, approval, budget, concurrency, idempotency and verification are
identical to local and MCP execution.

Treat instructions as versioned application assets. Record prompt and agent
versions or hashes in the invocation context and evaluation evidence, but do not
put prompt content, chat history, credentials or tool payloads in normal logs.

For explicit workflows, use the official Agent Framework checkpoint manager and
a trusted application-owned checkpoint store. Convert its `CheckpointInfo` to a
content-free NewHeap reference with
`INhAiAgentFrameworkWorkflowCheckpointAdapter`, then persist that reference in
the durable background operation. Resume only when adapter, workflow version,
session ID, checkpoint ID and state hash match. Stable workflow and executor IDs
are part of checkpoint compatibility; an incompatible topology or package
upgrade starts a new lineage rather than coercing old state.

## Avoid

- Granting an agent all permissions of its creator or accountable owner.
- Treating a tool allow-list, autonomy level, or prompt as authorization.
- Passing locally visible tools to Agent Framework without `Agent` discovery.
- Passing a raw catalog function or raw keyed model client around NewHeap governance.
- Adding Microsoft Agent Framework or provider dependencies to AI Common.
- Persisting authoritative run state only in conversation history.
- Loading workflow checkpoints from an untrusted store or resuming them under a changed identity.

## Verification

Use a deterministic `IChatClient`. Verify human contexts are rejected, agent and
model versions match, unauthorized tools are absent, observe-only autonomy hides
mutations, execute autonomy still preserves mutation safeguards, and content-safe
telemetry contains versions and outcomes only. Invoke the constructed agent and
prove a budget reservation and usage record occur. Reject an ungoverned catalog
and return a failed creation result for expected profile resolution failure.
Verify workflow references retain
the official session/checkpoint identity and reject a changed workflow version
or state hash. SPM-225, SPM-236, and
`NewHeap.Platform.AI.AgentFramework.Tests` are the executable references.
