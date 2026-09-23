# Use a named AI model profile

A model profile gives application code a stable name for a chat client and its
allowed capabilities, data classifications and execution regions.

## Register the client and profile

Reference `NewHeap.Platform.AI.Common` and your chosen provider's client package.
Given a configured `IChatClient` named `chatClient`, register it with DI:

```csharp
services.AddKeyedSingleton<IChatClient>("project-assistant-model", chatClient);
services.AddNewHeapPlatformAI(ai =>
{
    ai.AddChatProfile("project-assistant", profile => profile
        .UseKeyedClient("project-assistant-model")
        .RequireCapabilities(
            NhAiModelCapability.FunctionCalling,
            NhAiModelCapability.StructuredOutput)
        .PermitDataClassifications(NhAiDataClassification.Internal)
        .PermitExecutionRegions("local")
        .WithBudget(maxInputTokens: 4_096, maxOutputTokens: 1_024, maxCalls: 8)
        .WithTimeout(TimeSpan.FromSeconds(30)));
    ai.RequireProfile(
        "project-assistant",
        NhAiModelCapability.FunctionCalling,
        NhAiModelCapability.StructuredOutput);
});
```

Import `Microsoft.Extensions.AI`, `Microsoft.Extensions.DependencyInjection`
and `NewHeap.Platform.AI`. Set capabilities and region to match your client.
`RequireProfile` makes startup validate that the required profile is available.

## Resolve the profile before use

Inject `INhAiModelProfileResolver` into your service. Ask for the profile with
the requirements of this operation:

```csharp
var result = await resolver.ResolveChatAsync(new NhAiModelResolutionRequest(
    "project-assistant",
    NhAiModelCapability.FunctionCalling | NhAiModelCapability.StructuredOutput,
    NhAiDataClassification.Internal,
    "project-assistance",
    "local"));
```

Check `result.Success` before using `result.Data.Client` and propagate failures
to the caller. A successful result includes the selected profile and its limits.

## Try it without provider credentials

The [sample profile tests](../../examples/SampleProjectManagement/src/Back-end/Tests/SampleProjectManagement.Core.Tests/AiModelProfileSamplesTests.cs)
register `NhAiDeterministicChatClient` from `NewHeap.Platform.AI.Test`, resolve
the profile, and check its capabilities and token budget. From the repository root:

```sh
dotnet test examples/SampleProjectManagement/src/Back-end/Tests/SampleProjectManagement.Core.Tests --filter FullyQualifiedName~Named_project_assistant_profile_resolves_a_consumer_owned_chat_client
```

The [application registration](../../examples/SampleProjectManagement/src/Back-end/Libraries/SampleProjectManagement.Core/ServiceCollectionExtensions.cs)
shows the same profile alongside the sample's tools and context sources.

## Add capabilities as needed

- [Model profile reference](../consumer-guide/ai-model-profiles.md): fallbacks, budgets and startup validation.
- [Retrieval](../consumer-guide/ai-context-retrieval.md): supply authorized application context.
- [Protected actions](../consumer-guide/ai-protected-actions.md): require authorization and approval for mutations.
- [Agents](../consumer-guide/ai-agent-framework.md): connect the profile and tools to an agent.
