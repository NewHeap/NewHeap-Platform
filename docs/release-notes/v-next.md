# v-next

## NewHeap.Platform.AI.Common, NewHeap.Platform.AI.AgentFramework and NewHeap.Platform.AI.Chat: diagnosable model provider failures

- A model call that fails at the provider still returns `ai-model-dependency-unavailable`, and now says why
  without content: `NhAiModelProviderFailure.Of(result)` on the executor's result or failed stream completion,
  and `NhAiUsageRecord.ProviderFailure`, give the HTTP status, the provider's error code, the exception type
  and an `NhAiModelProviderFailureKind` (`Transient`, `Authentication`, `Request` or `Unknown`). The executor
  logs one warning with them and tags the `ai.model.invoke` activity (`http.response.status_code`,
  `newheap.ai.provider.code`, `newheap.ai.provider.failure_kind`); the provider's message stays out.
- The executor reads `HttpRequestException` itself. For an SDK that throws its own exception type, such as
  the OpenAI client's `ClientResultException`, register an `INhAiModelProviderFailureReader` with
  `AddModelProviderFailureReader<TReader>()`; a status it returns also decides the retry of a retry-eligible
  profile. SampleProjectManagement shows one for the OpenAI client (SPM-232).
- `NhAiAgentModelCallException` gains `ProviderFailure` and a constructor with an inner exception; the
  assistant logs a turn that ends on a failed model call with the code, status, provider code and kind.

| Breaking change | Required action |
|---|---|
| `NhAiAgentModelCallException` for a call that failed at the provider now keeps the provider's exception as `InnerException`, whose message can carry provider text. | Where provider text must not reach logs, log `Code` and `ProviderFailure` instead of the exception. |

## NewHeap.Platform.AI.Common: a busy tool waits for a free slot

- `INhAiToolConcurrencyLimiter.WaitToAcquireAsync` lets a limiter wait for a free slot. The default in-process
  limiter queues calls per partition, first come first served; its default implementation on the interface
  asks `TryAcquireAsync` once, so a custom or distributed limiter keeps its behavior until it implements it.
  NewHeap ships no distributed limiter.

| Breaking change | Required action |
|---|---|
| A tool call whose `MaxConcurrency` bound is full now waits for a free slot until the tool's `Timeout` or the invocation context's `Deadline`, on the application's `TimeProvider`, instead of failing at once with `ai-tool-concurrency-limited`, which it now gets only when that wait ends. | Where a caller relied on the immediate refusal, give the call a shorter `Deadline` or the tool a shorter `TimeoutSeconds`; in tests, move a test `TimeProvider` past the timeout instead of waiting. |
