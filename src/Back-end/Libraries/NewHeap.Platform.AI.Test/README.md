# NewHeap.Platform.AI.Test

Reusable test support for consumers of `NewHeap.Platform.AI.Common`. The
package contains deterministic `IChatClient` and embedding fakes, plus
authorized and denied invocation gates, bounded ingestion sources, captured
audit/usage sinks, and versioned fixtures that execute through
`Microsoft.Extensions.AI.Evaluation`. Persisted fixture reports contain hashes,
ratings and diagnostic counts rather than prompt, response, reason or
diagnostic content. The package contains no test runner and no `[Fact]` or
`[Theory]` tests.

## Scripted chat client

`NhAiScriptedChatClient` plays a fixed script of model responses so agent and
assistant flows can be tested end to end without a model provider:

```csharp
var model = new NhAiScriptedChatClient()
    .RespondWithFunctionCall("projects_search_v1", new { input = new { query = "roadmap", limit = 5 } })
    .RespondWithText("Two projects match the roadmap.");
services.AddKeyedSingleton<IChatClient>("project-assistant-chat-model", model);
```

- Every response that follows a function call first verifies that the incoming
  messages contain a `FunctionResultContent` for each issued call id; the
  verified results are available through `FunctionResults`.
- Streaming splits text into word chunks and ends with a `UsageContent` update
  with deterministic token counts; non-streaming returns the same response.
- An exhausted script throws `InvalidOperationException`. Pass `loop: true` to
  restart the script instead, for example in a runnable sample host.
- `Requests` records every received message list and the chat options.
