---
id: nh-ai-observability-evaluation
title: "Keep AI telemetry content-free and gate changes with evaluations"
area: backend
reference: ai-observability-evaluation
summary: "Execute model calls through bounded profile and budget policy, separate usage from audit, and compare versioned Microsoft AI Evaluation datasets before changing production defaults."
sample-cases: ["SPM-232", "SPM-233"]
public-symbols: ["INhAiChatExecutor", "NhAiChatRequest", "NhAiChatResult", "NhAiChatStream", "NhAiChatStreamCompletion", "INhAiUsageSink", "NhAiUsageRecord", "NhAiEvaluationDataset", "NhAiEvaluationRunner", "NhAiEvaluationReport"]
skills: ["newheap-backend-development", "newheap-testing"]
providers: ["provider-neutral"]
risk: high
---
## Preferred approach

Use `INhAiChatExecutor` when a model call needs NewHeap profile selection,
declared and remaining-run budgets, deadlines, content-free OpenTelemetry and
usage accounting. It returns standard Microsoft `ChatResponse` and
`ChatResponseUpdate` objects. Streaming resolution remains a structured result;
enumeration preserves cancellation and always records cleanup and time to first
token. After enumeration, await `NhAiChatStream.Completion`: successful end and
provider dependency failure are explicit `TaskResult` outcomes, while caller
cancellation remains cancellation.

Keep operational telemetry, application audit and usage accounting in separate
sinks. Usage records may include stable versions and hashes, bounded scope
identifiers, token counts, character counts, duration, finish reason and a model
identifier hash. They must not include prompts, responses, tool arguments,
retrieved documents, credentials or provider error bodies. Applications map the
retention categories to their own duration, residency, legal-hold and deletion
policies.

Use the packable AI Test fixtures with `Microsoft.Extensions.AI.Evaluation` for
versioned datasets and content-free reports. Treat missing or inconclusive metric
interpretations as failed. Deterministic evaluators belong in normal CI. Live
judge/model evaluations are explicit, credentialed, budgeted and reproducible;
compare and accept the baseline before changing a recommended model, prompt,
tool catalog or agent asset.

## Avoid

- Logging or tagging message text, model output, retrieved content or provider exception bodies.
- Combining business audit history and usage allocation into one universal platform database.
- Treating an inconclusive evaluation as a pass.
- Recommending a model upgrade based only on compilation or a single live demonstration.
- Keeping a streaming provider call alive after the consumer cancels enumeration.

## Verification

Capture activities, metrics, usage and reports and serialize them in tests.
Assert that protected fixture strings are absent while profile versions, hashes,
tokens, latency, time to first token, finish and cancellation outcomes remain.
Also force a provider exception during stream enumeration and assert that the
stream ends with a safe failed completion rather than leaking provider details.
Run the deterministic dataset on every relevant change, and run controlled live
evaluation only in its opt-in pipeline. SPM-232 and SPM-233 are the executable
references.
