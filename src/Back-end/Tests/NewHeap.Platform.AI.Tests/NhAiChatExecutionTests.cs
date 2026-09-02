using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace NewHeap.Platform.AI.Tests;

public sealed class NhAiChatExecutionTests
{
    [Fact]
    public async Task Chat_execution_records_usage_without_prompt_or_response_content()
    {
        const string prompt = "protected-customer-prompt";
        const string responseContent = "protected-model-response";
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, responseContent))
        {
            ModelId = "provider-model-deployment-42",
            FinishReason = ChatFinishReason.Stop,
            Usage = new UsageDetails
            {
                InputTokenCount = 11,
                OutputTokenCount = 7,
                CachedInputTokenCount = 3
            }
        };
        var client = new NhAiDeterministicChatClient();
        client.EnqueueResponse(response);
        var usageSink = new NhAiCapturedUsageSink();
        var services = CreateServices(client, usageSink);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider
            .GetRequiredService<INhAiChatExecutor>()
            .GetResponseAsync(CreateRequest(prompt));

        Assert.True(result.Success);
        Assert.Equal(responseContent, result.Data.Response.Text);
        var usage = Assert.Single(usageSink.Records);
        Assert.Equal(11, usage.InputTokens);
        Assert.Equal(7, usage.OutputTokens);
        Assert.Equal(3, usage.CachedInputTokens);
        Assert.Equal(NhAiRetentionCategory.UsageAggregate, usage.RetentionCategory);
        Assert.NotNull(usage.ModelIdHash);
        var serialized = JsonSerializer.Serialize(usage);
        Assert.DoesNotContain(prompt, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(responseContent, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("provider-model-deployment-42", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Profile_budget_failure_occurs_before_model_access()
    {
        var client = new NhAiDeterministicChatClient("unused");
        var services = CreateServices(client, new NhAiCapturedUsageSink());
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider
            .GetRequiredService<INhAiChatExecutor>()
            .GetResponseAsync(CreateRequest("bounded") with
            {
                RequestedOutputTokens = 1_001
            });

        Assert.False(result.Success);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task Expired_run_deadline_fails_before_model_access()
    {
        var client = new NhAiDeterministicChatClient("unused");
        var services = CreateServices(client, new NhAiCapturedUsageSink());
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var request = CreateRequest("bounded");

        var result = await scope.ServiceProvider
            .GetRequiredService<INhAiChatExecutor>()
            .GetResponseAsync(request with
            {
                InvocationContext = request.InvocationContext with
                {
                    Deadline = DateTimeOffset.UtcNow.AddSeconds(-1)
                }
            });

        Assert.False(result.Success);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task Streaming_cancellation_records_cleanup_without_buffering_content()
    {
        var client = new BlockingStreamingChatClient();
        var usageSink = new NhAiCapturedUsageSink();
        var services = CreateServices(client, usageSink);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        using var cancellation = new CancellationTokenSource();
        var start = await scope.ServiceProvider
            .GetRequiredService<INhAiChatExecutor>()
            .StartStreamingAsync(CreateRequest("protected-stream-prompt"));

        Assert.True(start.Success);
        await using var enumerator = start.Data.Updates.GetAsyncEnumerator(cancellation.Token);
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal("partial-protected-output", enumerator.Current.Text);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await enumerator.MoveNextAsync().AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await start.Data.Completion);

        var usage = Assert.Single(usageSink.Records);
        Assert.Equal(NhAiOutcomeKind.TerminalFailure, usage.Outcome);
        Assert.NotNull(usage.TimeToFirstToken);
        var serialized = JsonSerializer.Serialize(usage);
        Assert.DoesNotContain("protected-stream-prompt", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("partial-protected-output", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Streaming_dependency_failure_is_reported_through_task_result_completion()
    {
        var usageSink = new NhAiCapturedUsageSink();
        var services = CreateServices(new FailingStreamingChatClient(), usageSink);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var start = await scope.ServiceProvider
            .GetRequiredService<INhAiChatExecutor>()
            .StartStreamingAsync(CreateRequest("protected-stream-prompt"));

        Assert.True(start.Success);
        await foreach (var _ in start.Data.Updates)
        {
        }
        var completion = await start.Data.Completion;

        Assert.False(completion.Success);
        var usage = Assert.Single(usageSink.Records);
        Assert.Equal(NhAiOutcomeKind.DependencyUnavailable, usage.Outcome);
    }

    private static ServiceCollection CreateServices(
        IChatClient client,
        INhAiUsageSink usageSink)
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton("project-model", client);
        services.AddSingleton(usageSink);
        services.AddNewHeapPlatformAI(ai => ai.AddChatProfile(
            "project-assistant",
            profile => profile
                .UseKeyedClient("project-model")
                .RequireCapabilities(NhAiModelCapability.Chat)
                .PermitDataClassifications(NhAiDataClassification.Internal)
                .WithStreaming(NhAiStreamingPolicy.Allowed)
                .WithBudget(2_000, 1_000, 4, 1m))
            .UseBudgetManager<NhAiTestBudgetManager>());
        return services;
    }

    private static NhAiChatRequest CreateRequest(string prompt)
    {
        var context = new NhAiInvocationContext(
            "project-agent",
            "project-assistance",
            new Dictionary<string, string>())
        {
            ActorKind = NhAiActorKind.Agent,
            AccountableOwnerId = "owner-1",
            RunId = "run-1",
            CorrelationId = "correlation-1",
            PromptVersion = "1.0.0",
            PromptHash = new string('a', 64),
            ContextHash = new string('b', 64),
            ExecutionScopes = [new NhAiExecutionScopeEntry("division", "division-1")]
        };
        return new NhAiChatRequest(
            context,
            "project-assistant",
            NhAiModelCapability.Chat,
            NhAiDataClassification.Internal,
            [new ChatMessage(ChatRole.User, prompt)])
        {
            EstimatedInputTokens = 20,
            RequestedOutputTokens = 100,
            EstimatedCost = 0.01m,
            AgentId = "project-agent"
        };
    }

    private sealed class BlockingStreamingChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "partial-protected-output");
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return serviceKey is null && serviceType.IsInstanceOfType(this)
                ? this
                : null;
        }

        public void Dispose()
        {
        }
    }

    private sealed class FailingStreamingChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            throw new InvalidOperationException("protected-provider-detail");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return serviceKey is null && serviceType.IsInstanceOfType(this)
                ? this
                : null;
        }

        public void Dispose()
        {
        }
    }
}
