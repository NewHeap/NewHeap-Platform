# NewHeap.Platform.AI.Test

Reusable test support for consumers of `NewHeap.Platform.AI.Common`. The
package contains deterministic `IChatClient` and embedding fakes, plus
authorized and denied invocation gates, bounded ingestion sources, captured
audit/usage sinks, and versioned fixtures that execute through
`Microsoft.Extensions.AI.Evaluation`. Persisted fixture reports contain hashes,
ratings and diagnostic counts rather than prompt, response, reason or
diagnostic content. The package contains no test runner and no `[Fact]` or
`[Theory]` tests.

## OAuth authorization server for MCP connections

`NhAiTestAuthorizationServer` is an OAuth 2.1 authorization server on loopback for tests of MCP
connections: metadata (RFC 8414 and OpenID Connect), authorization with scripted approval or denial,
PKCE, the `iss` response parameter, refresh token rotation with reuse detection, client credentials,
`client_secret_basic`, `client_secret_post` and `private_key_jwt`, dynamic registration, Client ID
Metadata Documents and revocation. Access tokens are JWTs with the resource as audience.
`NhAiTestProtectedResource` puts protected resource metadata (RFC 9728) and bearer validation in front
of a test MCP server, with `401` challenges and `403 insufficient_scope` for missing scopes:

```csharp
await using var authorization = await NhAiTestAuthorizationServer.StartAsync();
authorization.RegisterClient("assistant", "client-secret");
var resource = new NhAiTestProtectedResource(authorization, mcpServerUrl, "/mcp");
app.Use((context, next) => resource.InvokeAsync(context, () => next(context)));
```

`TokenRequests`, `Revocations`, `DynamicClients` and `Requests` record what happened without tokens.

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
- `RespondWithProviderError("server_is_overloaded")` fails a response inside its
  stream with an `ErrorContent`, after optional text, as a provider does when it is
  overloaded or out of quota after the response started. The stream ends there.
- An exhausted script throws `InvalidOperationException`. Pass `loop: true` to
  restart the script instead, for example in a runnable sample host.
- `Requests` records every received message list and the chat options.

## Strict structured output

NewHeap passes a `ChatOptions.ResponseFormat` schema on unchanged; the application normalizes its
schemas for its providers. Set `StrictStructuredOutput` on `NhAiDeterministicChatClient` or
`NhAiScriptedChatClient` to refuse a schema as a provider in strict mode does: a `$ref` that names
neither the root (`#`) nor an entry of the root's `$defs`, an object without
`additionalProperties: false`, or a property missing from `required`. The call throws an
`HttpRequestException` with status 400 and consumes no response; a stream throws when it starts.
`NhAiStrictStructuredOutput.Validate` returns every violation with its JSON pointer:

```csharp
var model = new NhAiDeterministicChatClient("""{"open":[],"done":[]}""")
{
    StrictStructuredOutput = true
};
Assert.Empty(NhAiStrictStructuredOutput.Validate(format.Schema!.Value));
```
