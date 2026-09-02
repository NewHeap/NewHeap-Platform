---
id: nh-ai-model-profiles
title: "Register named AI model profiles"
area: backend
reference: ai-model-profiles
summary: "Keep provider clients consumer-owned and resolve them through stable NewHeap profiles with explicit capabilities, data policy, budgets, and startup requirements."
sample-cases: ["SPM-220"]
public-symbols: ["INhAiModelProfileRegistry", "INhAiModelProfileResolver", "NhAiBuilder", "NhAiDataClassification", "NhAiDeterministicChatClient", "NhAiDeterministicEmbeddingGenerator", "NhAiModelBudget", "NhAiModelCapability", "NhAiModelProfile", "NhAiModelProfileBuilder", "NhAiModelResolutionRequest", "NhAiResolvedChatProfile", "NhAiServiceCollectionExtensions", "NhAiStreamingPolicy"]
skills: ["newheap-backend-development"]
providers: ["provider-neutral"]
risk: high
---
## Preferred approach

Register the provider-backed `Microsoft.Extensions.AI.IChatClient` as a keyed
consumer service. Use `AddNewHeapPlatformAI` and `AddChatProfile` to map a stable
lowercase dash-case application profile name to that key. Declare the required
capabilities, permitted data classifications and execution regions, bounded
token/call/cost budget, timeout, streaming policy, fallbacks, routing tags, and
evaluation baseline that match the application's intent.

Use `RequireProfile` for profiles and capabilities the host must have at
startup. Resolve a client through `INhAiModelProfileResolver` with an explicit
purpose, required capabilities, classification, and execution region. Treat the
bounded decision trace as operational metadata; it contains no provider secret
or prompt content. Register optional audit, usage, budget, and context
contributors at composition boundaries.

Keep provider packages, endpoints, credentials, and concrete model names in the
consumer. Use `NhAiDeterministicChatClient` and
`NhAiDeterministicEmbeddingGenerator` in normal tests. Live-provider tests are
opt-in and require their own credentials, data policy, and budget controls.

## Avoid

- Copying an API key, endpoint, provider options, prompt, or model response into a NewHeap profile.
- Resolving a keyed client directly in application workflows and bypassing profile policy.
- Inferring capabilities from a model name or accepting model output as authorization.
- Registering a fallback that weakens classification, residency, or capability requirements.
- Logging request or response content in a profile decision, audit record, or usage record.
- Making a live-provider call part of deterministic unit or sample tests.

## Verification

Start the service provider and verify missing keyed clients, required profiles,
required capabilities, fallback targets, and fallback cycles fail clearly.
Resolve the named profile for allowed and denied classifications, capabilities,
and regions. Prove repeated identical registration is idempotent and conflicting
registration is rejected. SPM-220 and `NewHeap.Platform.AI.Tests` provide the
executable deterministic references.
