---
id: nh-ai-assets
title: "Version AI instruction and agent assets without a prompt DSL"
area: backend
reference: ai-assets
summary: "Keep instructions as application-owned text while recording a stable content-free manifest for behavioral versioning, approval binding, evaluation, and rollout evidence."
sample-cases: ["SPM-227"]
public-symbols: ["NhAiAssetManifest", "NhAiAssetRole", "NhAiTextAsset", "NhAiTextAssetFactory"]
skills: ["newheap-backend-development"]
providers: ["provider-neutral"]
risk: medium
---
## Preferred approach

Keep system instructions, developer instructions and declarative agent assets as
normal embedded or application-owned text. Create a `NhAiTextAsset` with a stable
lowercase ID and version. Its manifest records the SHA-256 content hash, source
provenance, role, trust, required model capabilities, required tool contracts,
context policy, data classification, retention category and evaluation baseline.

Carry the asset version and hash in `NhAiInvocationContext`, agent descriptors,
proposal hashes, approvals, audit metadata and evaluation reports. A behavioral
change creates a new version and requires accepted evaluation evidence before it
becomes the recommended production default. Use deprecation/replacement metadata
for an intentional transition; do not silently mutate a published version.

The asset content is runtime input, not logging metadata. Normal telemetry,
usage, audit and control-plane synchronization contain only stable IDs, versions
and hashes. Provider credentials, conversation history, retrieved context and
tool arguments remain outside the asset manifest.

## Avoid

- Inventing a proprietary prompt language or hiding identity and policy in text.
- Changing content without changing the version/hash evidence.
- Logging or centrally synchronizing full instructions by default.
- Treating instructions as capability, approval or authorization evidence.
- Promoting a model or prompt change without its evaluation baseline.

## Verification

Assert deterministic hashes, bounded manifest fields, required tool contracts,
prompt version/hash propagation into agent and proposal evidence, invalidation
after a hash change, and absence of asset content from serialized manifest,
audit and usage records. SPM-227 is the executable reference.
