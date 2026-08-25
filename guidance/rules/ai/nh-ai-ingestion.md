---
id: nh-ai-ingestion
title: "Authorize and scope AI ingestion before reading content"
area: backend
reference: ai-ingestion
summary: "Use the standard Microsoft VectorData abstractions behind an authorization-first ingestion pipeline that preserves scope, provenance, classification and deterministic replacement identity."
sample-cases: ["SPM-231", "SPM-235"]
public-symbols: ["INhAiIngestionPipeline", "INhAiIngestionSource", "INhAiIngestionAuthorizationPolicy", "INhAiIngestionVersionManager", "NhAiIngestionVersionDecisionKind", "NhAiIngestionVersionLease", "NhAiIngestionVersionRequest", "NhAiIngestionRequest", "NhAiIngestionBatchRequest", "NhAiIngestionDeletionRequest", "NhAiTestIngestionVersionManager", "NhAiVectorRecord", "INhAiBackgroundOperationIngestionAdapter", "NhAiDurableIngestionCheckpoint"]
skills: ["newheap-backend-development", "newheap-background-processing"]
providers: ["provider-neutral"]
risk: high
---
## Preferred approach

Register application-owned ingestion sources and an authorization policy. The
pipeline authorizes the actor and execution scope before opening a document,
then validates bounded content, source identity, classification, trust,
provenance and a canonical content hash. It resolves an embedding profile,
reserves the model call through `INhAiBudgetManager`, creates deterministic
chunk IDs and writes the resulting records through a
keyed `Microsoft.Extensions.VectorData.VectorStore`.

Register a durable application-owned `INhAiIngestionVersionManager`. It owns the
immutable `(source, document, version)` decision and records the canonical
content hash plus idempotency key before vector writes. A matching retry is a
duplicate, while a changed hash or key is a conflict; neither may overwrite an
existing version or start an embedding call. The default manager denies
ingestion.

Keep the provider and collection application-owned. Every stored record carries
filterable scope keys, provenance, classification, trust, document version and
the ingestion idempotency key. Apply those scope filters inside the provider
query when retrieving records; never fetch broadly and filter after materialization.

Run long ingestion work through `INhAiBackgroundOperationIngestionAdapter`. It
binds the durable operation identity, idempotency key and fencing token, then
stores only a content-free `NhAiDurableIngestionCheckpoint`. Re-entry repeats
authorization and returns the checkpointed result without reading the source or
regenerating embeddings. A request-contract mismatch fails closed.

Declare replacement lineage with the previous version, content hash and chunk
count. Replacement requires `CanDeleteAsync` before the source is opened or any
embedding/vector work begins. After that decision, the pipeline upserts
deterministic new-version keys before idempotently deleting the obsolete keys.
Use `DeleteAsync` for authorized source deletion,
and `IngestBatchAsync` when callers need per-document `TaskResult` evidence and
partial-success counts without losing completed documents.

`Microsoft.Extensions.DataIngestion` remains preview in the pinned baseline.
Keep any adapter in an optional package and do not expose preview types through
stable NewHeap contracts until its maturity and compatibility are proven.

## Avoid

- Reading a stream or document before source and scope authorization succeeds.
- Treating read authorization as permission to replace or delete prior vectors.
- Storing an idempotency key only as vector metadata without an immutable version decision.
- Inventing a second vector database API instead of using `VectorStore` and `VectorStoreCollection`.
- Treating retrieved text as trusted instructions.
- Omitting scope keys or applying tenant filters only after vector results are materialized.
- Claiming an in-memory connector as SQL Server or PostgreSQL provider evidence.
- Persisting credentials, raw prompts or full documents in ingestion checkpoints or telemetry.

## Verification

Assert that denial causes zero source reads. For allowed ingestion, verify the
canonical document hash, deterministic chunks, embedding dimensions, keyed
VectorData collection, scope keys, provenance, replacement/deletion behavior
and idempotency metadata. Prove read-allowed/delete-denied replacement causes no
source, embedding, upsert, or delete calls, and prove a conflicting version/key
cannot reserve model budget, generate embeddings, or write vectors twice. Denied
embedding budget must release its version lease and return a safe result. Verify
partial batches preserve successful items and
durable re-entry performs authorization but no second source read, embedding or
upsert. Add real provider tests only when NewHeap owns provider-specific
configuration, SQL or query behavior. SPM-231 and SPM-235 are the
provider-neutral executable references.
