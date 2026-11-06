using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Models;
using Xunit;

namespace NewHeap.Platform.AI.Tests;

/// <summary>
/// A model call that fails at the provider keeps the failure code
/// <see cref="NhAiModelFailureCodes.DependencyUnavailable"/> and tells why without content: its status,
/// provider code and kind reach the failed result, the usage record and a warning log, never the
/// provider's message.
/// </summary>
public sealed class NhAiModelProviderFailureTests
{
    private const string ProviderMessage = "protected-provider-message: the prompt was 'protected-prompt'";

    [Fact]
    public async Task Rejected_credentials_reach_the_result_usage_record_and_log_without_the_provider_message()
    {
        var client = new FailingChatClient(() => new HttpRequestException(ProviderMessage, null, HttpStatusCode.Unauthorized));
        var usage = new NhAiCapturedUsageSink();
        var logs = new CapturedLogs();
        await using var provider = CreateServices(client, usage, logs, retryEligible: true).BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var result = await scope.ServiceProvider
            .GetRequiredService<INhAiChatExecutor>()
            .GetResponseAsync(CreateRequest());

        Assert.False(result.Success);
        Assert.Equal(NhAiModelFailureCodes.DependencyUnavailable, Assert.Single(result.GetResultItems()).Name);
        var failure = NhAiModelProviderFailure.Of(result);
        Assert.Equal(
            new NhAiModelProviderFailure(
                NhAiModelProviderFailureKind.Authentication,
                401,
                null,
                typeof(HttpRequestException).FullName!),
            failure);
        // Rejected credentials are not transient: the retry-eligible profile calls the provider once.
        Assert.Equal(1, client.Calls);
        var record = Assert.Single(usage.Records);
        Assert.Equal(NhAiOutcomeKind.DependencyUnavailable, record.Outcome);
        Assert.Equal(failure, record.ProviderFailure);
        var warning = Assert.Single(logs.Warnings);
        Assert.Contains("Authentication", warning, StringComparison.Ordinal);
        Assert.Contains("status 401", warning, StringComparison.Ordinal);
        Assert.Contains(typeof(HttpRequestException).FullName!, warning, StringComparison.Ordinal);
        Assert.DoesNotContain("protected", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("protected", string.Join(" ", result.AllErrorMessages), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, NhAiModelProviderFailureKind.Request)]
    [InlineData(HttpStatusCode.NotFound, NhAiModelProviderFailureKind.Request)]
    [InlineData(HttpStatusCode.Forbidden, NhAiModelProviderFailureKind.Authentication)]
    [InlineData(HttpStatusCode.TooManyRequests, NhAiModelProviderFailureKind.Transient)]
    [InlineData(HttpStatusCode.ServiceUnavailable, NhAiModelProviderFailureKind.Transient)]
    public async Task The_kind_follows_the_provider_status(HttpStatusCode status, NhAiModelProviderFailureKind kind)
    {
        var client = new FailingChatClient(() => new HttpRequestException(ProviderMessage, null, status));
        await using var provider = CreateServices(client, new NhAiCapturedUsageSink(), null).BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var result = await scope.ServiceProvider
            .GetRequiredService<INhAiChatExecutor>()
            .GetResponseAsync(CreateRequest());

        var failure = NhAiModelProviderFailure.Of(result);
        Assert.NotNull(failure);
        Assert.Equal((kind, (int)status), (failure.Kind, failure.StatusCode));
    }

    [Fact]
    public async Task A_call_without_an_answer_reports_its_http_request_error_as_transient()
    {
        var client = new FailingChatClient(() => new HttpRequestException(HttpRequestError.ConnectionError, ProviderMessage));
        await using var provider = CreateServices(client, new NhAiCapturedUsageSink(), null).BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var result = await scope.ServiceProvider
            .GetRequiredService<INhAiChatExecutor>()
            .GetResponseAsync(CreateRequest());

        var failure = NhAiModelProviderFailure.Of(result);
        Assert.NotNull(failure);
        Assert.Equal(
            (NhAiModelProviderFailureKind.Transient, (int?)null, "ConnectionError"),
            (failure.Kind, failure.StatusCode, failure.ProviderCode));
    }

    [Fact]
    public async Task An_exception_nothing_knows_is_unknown_and_names_only_its_type()
    {
        var client = new FailingChatClient(() => new InvalidOperationException(ProviderMessage));
        await using var provider = CreateServices(client, new NhAiCapturedUsageSink(), null).BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var result = await scope.ServiceProvider
            .GetRequiredService<INhAiChatExecutor>()
            .GetResponseAsync(CreateRequest());

        Assert.Equal(
            new NhAiModelProviderFailure(
                NhAiModelProviderFailureKind.Unknown,
                null,
                null,
                typeof(InvalidOperationException).FullName!),
            NhAiModelProviderFailure.Of(result));
    }

    [Fact]
    public async Task A_registered_reader_supplies_the_status_and_bounded_code_of_an_sdk_exception()
    {
        var client = new FailingChatClient(() => new WrappingException(
            new ProviderSdkException(403, "invalid api key!" + new string('x', 100))));
        await using var provider = CreateServices(
                client,
                new NhAiCapturedUsageSink(),
                null,
                ai => ai.AddModelProviderFailureReader<ProviderSdkFailureReader>())
            .BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var result = await scope.ServiceProvider
            .GetRequiredService<INhAiChatExecutor>()
            .GetResponseAsync(CreateRequest());

        // The reader answers for the inner SDK exception of the chain; the code keeps 64 safe characters.
        var failure = NhAiModelProviderFailure.Of(result);
        Assert.NotNull(failure);
        Assert.Equal(NhAiModelProviderFailureKind.Authentication, failure.Kind);
        Assert.Equal(403, failure.StatusCode);
        Assert.Equal("invalid-api-key-" + new string('x', 48), failure.ProviderCode);
        Assert.Equal(typeof(ProviderSdkException).FullName, failure.ExceptionType);
    }

    [Fact]
    public async Task A_transient_status_from_a_reader_is_retried_by_a_retry_eligible_profile()
    {
        var client = new FailingChatClient(() => new ProviderSdkException(503, "server_is_overloaded"), failures: 1);
        await using var provider = CreateServices(
                client,
                new NhAiCapturedUsageSink(),
                null,
                ai => ai.AddModelProviderFailureReader<ProviderSdkFailureReader>(),
                retryEligible: true)
            .BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var result = await scope.ServiceProvider
            .GetRequiredService<INhAiChatExecutor>()
            .GetResponseAsync(CreateRequest());

        Assert.True(result.Success);
        Assert.Null(NhAiModelProviderFailure.Of(result));
        Assert.Equal(2, client.Calls);
    }

    [Fact]
    public async Task A_stream_that_fails_at_the_provider_carries_the_failure_on_its_completion()
    {
        var client = new FailingChatClient(() => new HttpRequestException(ProviderMessage, null, HttpStatusCode.BadRequest));
        var usage = new NhAiCapturedUsageSink();
        var logs = new CapturedLogs();
        await using var provider = CreateServices(client, usage, logs).BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var start = await scope.ServiceProvider
            .GetRequiredService<INhAiChatExecutor>()
            .StartStreamingAsync(CreateRequest());
        Assert.True(start.Success);

        await foreach (var _ in start.Data.Updates)
        {
        }
        var completion = await start.Data.Completion;

        Assert.False(completion.Success);
        Assert.Equal(NhAiModelFailureCodes.DependencyUnavailable, Assert.Single(completion.GetResultItems()).Name);
        var failure = NhAiModelProviderFailure.Of(completion);
        Assert.NotNull(failure);
        Assert.Equal((NhAiModelProviderFailureKind.Request, (int?)400), (failure.Kind, failure.StatusCode));
        Assert.Equal(failure, Assert.Single(usage.Records).ProviderFailure);
        Assert.DoesNotContain("protected", Assert.Single(logs.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_copied_result_keeps_the_code_but_not_the_provider_failure()
    {
        var client = new FailingChatClient(() => new HttpRequestException(ProviderMessage, null, HttpStatusCode.Unauthorized));
        var usage = new NhAiCapturedUsageSink();
        await using var provider = CreateServices(client, usage, null).BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var result = await scope.ServiceProvider
            .GetRequiredService<INhAiChatExecutor>()
            .GetResponseAsync(CreateRequest());
        var copied = TaskResult<string>.Failed(result);

        Assert.Equal(NhAiModelFailureCodes.DependencyUnavailable, Assert.Single(copied.GetResultItems()).Name);
        Assert.Null(NhAiModelProviderFailure.Of(copied));
        Assert.NotNull(NhAiModelProviderFailure.Of(result));
    }

    private static ServiceCollection CreateServices(
        IChatClient client,
        NhAiCapturedUsageSink usage,
        CapturedLogs? logs,
        Action<NhAiBuilder>? configure = null,
        bool retryEligible = false)
    {
        var services = new ServiceCollection();
        if (logs is not null)
        {
            services.AddLogging(logging => logging.AddProvider(logs));
        }
        services.AddKeyedSingleton("provider-model", client);
        services.AddSingleton<INhAiUsageSink>(usage);
        services.AddNewHeapPlatformAI(ai =>
        {
            ai.AddChatProfile("project-assistant", profile => profile
                    .UseKeyedClient("provider-model")
                    .RequireCapabilities(NhAiModelCapability.Chat)
                    .PermitDataClassifications(NhAiDataClassification.Internal)
                    .WithStreaming(NhAiStreamingPolicy.Allowed)
                    .WithBudget(2_000, 1_000, 4, 1m)
                    .AllowSafeRetry(retryEligible))
                .UseBudgetManager<NhAiTestBudgetManager>();
            configure?.Invoke(ai);
        });
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
            AccountableOwnerId = "owner-1"
        };
        return new NhAiChatRequest(
            context,
            "project-assistant",
            NhAiModelCapability.Chat,
            NhAiDataClassification.Internal,
            [new ChatMessage(ChatRole.User, "protected-prompt")])
        {
            EstimatedInputTokens = 20,
            RequestedOutputTokens = 100
        };
    }

    /// <summary>
    /// An exception of a provider SDK that NewHeap does not know, with its own status and code.
    /// </summary>
    private sealed class ProviderSdkException(int status, string errorCode) : Exception(ProviderMessage)
    {
        public int Status { get; } = status;

        public string ErrorCode { get; } = errorCode;
    }

    private sealed class WrappingException(Exception inner) : Exception(ProviderMessage, inner);

    private sealed class ProviderSdkFailureReader : INhAiModelProviderFailureReader
    {
        public NhAiModelProviderError? Read(Exception exception)
        {
            return exception is ProviderSdkException sdk
                ? new NhAiModelProviderError(sdk.Status, sdk.ErrorCode)
                : null;
        }
    }

    /// <summary>
    /// Throws the exception the factory makes for the first <c>failures</c> calls, then answers.
    /// </summary>
    private sealed class FailingChatClient(Func<Exception> failure, int failures = int.MaxValue) : IChatClient
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) <= failures)
            {
                throw failure();
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "answered")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            if (Interlocked.Increment(ref _calls) <= failures)
            {
                throw failure();
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant, "answered");
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

    private sealed class CapturedLogs : ILoggerProvider
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<string> Warnings
        {
            get
            {
                lock (_entries)
                {
                    return _entries
                        .Where(entry => entry.Level == LogLevel.Warning)
                        .Select(entry => entry.Message)
                        .ToArray();
                }
            }
        }

        public ILogger CreateLogger(string categoryName)
        {
            return new Logger(this);
        }

        public void Dispose()
        {
        }

        private sealed class Logger(CapturedLogs logs) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
            {
                return null;
            }

            public bool IsEnabled(LogLevel logLevel)
            {
                return true;
            }

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (logs._entries)
                {
                    logs._entries.Add((logLevel, formatter(state, exception) + (exception is null ? string.Empty : " " + exception)));
                }
            }
        }
    }
}
