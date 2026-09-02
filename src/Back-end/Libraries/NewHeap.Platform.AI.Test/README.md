# NewHeap.Platform.AI.Test

Reusable test support for consumers of `NewHeap.Platform.AI.Common`. The
package contains deterministic `IChatClient` and embedding fakes, plus
authorized and denied invocation gates, bounded ingestion sources, captured
audit/usage sinks, and versioned fixtures that execute through
`Microsoft.Extensions.AI.Evaluation`. Persisted fixture reports contain hashes,
ratings and diagnostic counts rather than prompt, response, reason or
diagnostic content. The package contains no test runner and no `[Fact]` or
`[Theory]` tests.
