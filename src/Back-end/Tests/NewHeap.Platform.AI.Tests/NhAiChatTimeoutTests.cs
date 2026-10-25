using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace NewHeap.Platform.AI.Tests;

/// <summary>
/// A governed deadline, the profile's timeout or the invocation context's deadline, is the failed
/// result <see cref="NhAiModelFailureCodes.Timeout"/>; the caller's own cancellation still throws.
/// Both reserve the budget once and write one usage record and one activity for the call (LS39).
/// </summary>
public sealed class NhAiChatTimeoutTests
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(200);

    [Fact]
    public async Task A_profile_timeout_is_a_failed_result_with_a_timed_out_usage_record()
    {
        var client = new UnansweredChatClient();
        using var activities = new ModelActivityCapture();
        await using var provider = CreateServices(client, ShortTimeout).BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var result = await scope.ServiceProvider
            .GetRequiredService<INhAiChatExecutor>()
            .GetResponseAsync(CreateRequest());

        Assert.False(result.Success);
        Assert.Equal(NhAiModelFailureCodes.Timeout, Assert.Single(result.GetResultItems()).Name);
        Assert.True(client.SawCancellation);
        var usage = AssertSettledOnce(scope.ServiceProvider, NhAiOutcomeKind.TimedOut, NhAiModelFinishReasons.TimedOut);
        var activity = activities.Single(usage.ModelCallId);
        Assert.Equal(nameof(NhAiOutcomeKind.TimedOut), activity.GetTagItem("newheap.ai.outcome"));
        Assert.Equal(NhAiModelFinishReasons.TimedOut, activity.GetTagItem("gen_ai.response.finish_reasons"));
    }

    [Fact]
    public async Task The_invocation_deadline_is_a_failed_result_as_well()
    {
        var client = new UnansweredChatClient();
        await using var provider = CreateServices(client, timeout: null).BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var request = CreateRequest();

        // The profile allows a minute; the run's deadline ends the call first.
        var result = await scope.ServiceProvider
            .GetRequiredService<INhAiChatExecutor>()
            .GetResponseAsync(
                request with
                {
                    InvocationContext = request.InvocationContext with
                    {
                        Deadline = DateTimeOffset.UtcNow.AddSeconds(1)
                    }
                });

        Assert.False(result.Success);
        Assert.Equal(NhAiModelFailureCodes.Timeout, Assert.Single(result.GetResultItems()).Name);
        AssertSettledOnce(scope.ServiceProvider, NhAiOutcomeKind.TimedOut, NhAiModelFinishReasons.TimedOut);
    }

    [Fact]
    public async Task A_cancellation_the_caller_did_not_request_counts_as_a_timeout()
    {
        // An HTTP client's own timeout cancels the provider request without the caller's token.
        await using var provider = CreateServices(new ProviderTimeoutChatClient(), timeout: null).BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var result = await scope.ServiceProvider
            .GetRequiredService<INhAiChatExecutor>()
            .GetResponseAsync(CreateRequest());

        Assert.False(result.Success);
        Assert.Equal(NhAiModelFailureCodes.Timeout, Assert.Single(result.GetResultItems()).Name);
        AssertSettledOnce(scope.ServiceProvider, NhAiOutcomeKind.TimedOut, NhAiModelFinishReasons.TimedOut);
    }

    [Fact]
    public async Task A_caller_cancellation_still_throws_and_settles_the_call()
    {
        var client = new UnansweredChatClient();
        await using var provider = CreateServices(client, timeout: null).BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        using var cancellation = new CancellationTokenSource();

        var call = scope.ServiceProvider
            .GetRequiredService<INhAiChatExecutor>()
            .GetResponseAsync(CreateRequest(), cancellation.Token);
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        AssertSettledOnce(scope.ServiceProvider, NhAiOutcomeKind.TerminalFailure, NhAiModelFinishReasons.Cancelled);
    }

    [Fact]
    public async Task A_stream_that_reaches_its_timeout_ends_with_a_failed_completion()
    {
        var client = new UnansweredChatClient(firstUpdate: "partial-output");
        using var activities = new ModelActivityCapture();
        await using var provider = CreateServices(client, ShortTimeout).BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var start = await scope.ServiceProvider
            .GetRequiredService<INhAiChatExecutor>()
            .StartStreamingAsync(CreateRequest());
        Assert.True(start.Success);

        // The enumeration ends without an exception; the completion says why.
        var text = string.Empty;
        await foreach (var update in start.Data.Updates)
        {
            text += update.Text;
        }
        var completion = await start.Data.Completion;

        Assert.Equal("partial-output", text);
        Assert.False(completion.Success);
        Assert.Equal(NhAiModelFailureCodes.Timeout, Assert.Single(completion.GetResultItems()).Name);
        var usage = AssertSettledOnce(scope.ServiceProvider, NhAiOutcomeKind.TimedOut, NhAiModelFinishReasons.TimedOut);
        Assert.NotNull(usage.TimeToFirstToken);
        Assert.Equal(nameof(NhAiOutcomeKind.TimedOut), activities.Single(usage.ModelCallId).GetTagItem("newheap.ai.outcome"));
    }

    [Fact]
    public async Task A_caller_cancellation_of_a_stream_still_throws_and_settles_the_call()
    {
        var client = new UnansweredChatClient(firstUpdate: "partial-output");
        await using var provider = CreateServices(client, timeout: null).BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        using var cancellation = new CancellationTokenSource();
        var start = await scope.ServiceProvider
            .GetRequiredService<INhAiChatExecutor>()
            .StartStreamingAsync(CreateRequest());
        Assert.True(start.Success);

        await using var enumerator = start.Data.Updates.GetAsyncEnumerator(cancellation.Token);
        Assert.True(await enumerator.MoveNextAsync());
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await enumerator.MoveNextAsync());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start.Data.Completion);
        AssertSettledOnce(scope.ServiceProvider, NhAiOutcomeKind.TerminalFailure, NhAiModelFinishReasons.Cancelled);
    }

    /// <summary>
    /// One budget reservation and one usage record for the call, with the same model call id: the
    /// record a budget manager settles the reservation by.
    /// </summary>
    private static NhAiUsageRecord AssertSettledOnce(
        IServiceProvider services,
        NhAiOutcomeKind outcome,
        string finishReason)
    {
        var reservation = Assert.Single(
            ((NhAiTestBudgetManager)services.GetRequiredService<INhAiBudgetManager>()).Requests);
        var usage = Assert.Single(services.GetRequiredService<NhAiCapturedUsageSink>().Records);
        Assert.Equal(reservation.ModelCallId, usage.ModelCallId);
        Assert.Equal(outcome, usage.Outcome);
        Assert.Equal(finishReason, usage.FinishReason);
        Assert.Equal(0, usage.OutputTokens);
        return usage;
    }

    private static ServiceCollection CreateServices(IChatClient client, TimeSpan? timeout)
    {
        var services = new ServiceCollection();
        var usage = new NhAiCapturedUsageSink();
        services.AddKeyedSingleton("timeout-model", client);
        services.AddSingleton(usage);
        services.AddSingleton<INhAiUsageSink>(usage);
        services.AddNewHeapPlatformAI(ai => ai.AddChatProfile(
                "timeout-assistant",
                profile =>
                {
                    profile
                        .UseKeyedClient("timeout-model")
                        .RequireCapabilities(NhAiModelCapability.Chat)
                        .PermitDataClassifications(NhAiDataClassification.Internal)
                        .WithStreaming(NhAiStreamingPolicy.Allowed)
                        .WithBudget(2_000, 1_000, 4, 1m);
                    if (timeout is { } value)
                    {
                        profile.WithTimeout(value);
                    }
                })
            .UseBudgetManager<NhAiTestBudgetManager>());
        return services;
    }

    private static NhAiChatRequest CreateRequest()
    {
        var context = new NhAiInvocationContext(
            "project-agent",
            "project-assistance",
            new Dictionary<string, string>())
        {
            ActorKind = NhAiActorKind.Agent,
            AccountableOwnerId = "owner-1",
            RunId = "run-1"
        };
        return new NhAiChatRequest(
            context,
            "timeout-assistant",
            NhAiModelCapability.Chat,
            NhAiDataClassification.Internal,
            [new ChatMessage(ChatRole.User, "Answer slowly.")])
        {
            EstimatedInputTokens = 20,
            RequestedOutputTokens = 100,
            AgentId = "project-agent"
        };
    }

    /// <summary>
    /// A model that never answers: it waits until the call's token is cancelled, after an optional
    /// first update of a stream.
    /// </summary>
    private sealed class UnansweredChatClient(string? firstUpdate = null) : IChatClient
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool SawCancellation { get; private set; }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                SawCancellation = true;
                throw;
            }

            throw new UnreachableException();
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            if (firstUpdate is not null)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, firstUpdate);
            }

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

    private sealed class ProviderTimeoutChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            throw new TaskCanceledException("The provider request timed out.", new TimeoutException());
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return null;
        }

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// The <c>ai.model.invoke</c> activities that stop while the capture lives; other tests run in
    /// parallel, so a test finds its own by model call id.
    /// </summary>
    private sealed class ModelActivityCapture : IDisposable
    {
        private readonly ConcurrentQueue<Activity> _stopped = new();
        private readonly ActivityListener _listener;

        public ModelActivityCapture()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == "NewHeap.Platform.AI",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity => _stopped.Enqueue(activity)
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public Activity Single(Guid modelCallId)
        {
            return Assert.Single(
                _stopped,
                activity => Equals(activity.GetTagItem("newheap.ai.model_call_id"), modelCallId));
        }

        public void Dispose()
        {
            _listener.Dispose();
        }
    }
}
