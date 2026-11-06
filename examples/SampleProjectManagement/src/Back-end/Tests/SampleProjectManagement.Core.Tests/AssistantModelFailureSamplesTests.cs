using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI;
using NewHeap.Platform.AI.Test;
using OpenAI;
using SampleProjectManagement.Api.Composition;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// Executable evidence that a model call the provider refuses is diagnosable without content (SPM-232):
/// the OpenAI client's refusal keeps the failure code <c>ai-model-dependency-unavailable</c> while the
/// sample's <see cref="SampleOpenAiFailureReader"/> gives its status, error code and kind to the result,
/// the usage record and the warning log, never the provider's message. The OpenAI-compatible endpoint is
/// a stub in this process; no test calls a model.
/// </summary>
public sealed class AssistantModelFailureSamplesTests
{
    private const string ModelKey = "sample-openai-model";
    private const string ProfileName = "sample-openai";

    /// <summary>OpenAI's answer to a wrong key; its message repeats part of the key.</summary>
    private const string RefusedKeyAnswer =
        """{"error":{"message":"Incorrect API key provided: sample-****-key.","type":"invalid_request_error","param":null,"code":"invalid_api_key"}}""";

    /// <summary>A generated test value; it is no key of any service.</summary>
    private static readonly string TestApiKey = "sample-test-key-" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task SPM_232_a_refused_key_reports_its_status_and_error_code_without_the_provider_message()
    {
        var logs = new CapturedLogs();
        var usage = new NhAiCapturedUsageSink();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddProvider(logs));
        services.AddKeyedSingleton(ModelKey, RefusingOpenAiClient(HttpStatusCode.Unauthorized, RefusedKeyAnswer));
        services.AddSingleton<INhAiUsageSink>(usage);
        services.AddNewHeapPlatformAI(ai => ai
            .AddChatProfile(ProfileName, profile => profile
                .UseKeyedClient(ModelKey)
                .RequireCapabilities(NhAiModelCapability.Chat)
                .PermitDataClassifications(NhAiDataClassification.Internal)
                .WithBudget(2_000, 500, 2)
                .AllowSafeRetry())
            .UseBudgetManager<NhAiTestBudgetManager>()
            .AddModelProviderFailureReader<SampleOpenAiFailureReader>());
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var result = await scope.ServiceProvider
            .GetRequiredService<INhAiChatExecutor>()
            .GetResponseAsync(Request(), TestContext.Current.CancellationToken);

        // The code stays the same for every provider failure; the failure tells why.
        Assert.False(result.Success);
        Assert.Equal(NhAiModelFailureCodes.DependencyUnavailable, Assert.Single(result.GetResultItems()).Name);
        var failure = NhAiModelProviderFailure.Of(result);
        Assert.Equal(
            new NhAiModelProviderFailure(
                NhAiModelProviderFailureKind.Authentication,
                401,
                "invalid_api_key",
                typeof(ClientResultException).FullName!),
            failure);
        Assert.Equal(failure, Assert.Single(usage.Records).ProviderFailure);
        var warning = Assert.Single(logs.Warnings);
        Assert.Contains("status 401, provider code invalid_api_key", warning, StringComparison.Ordinal);
        Assert.DoesNotContain(logs.All, entry => entry.Contains("Incorrect API key", StringComparison.Ordinal));
        Assert.DoesNotContain("Incorrect API key", string.Join(" ", result.AllErrorMessages), StringComparison.Ordinal);
    }

    [Fact]
    public void SPM_232_the_sample_assistant_registers_the_reader_of_its_openai_compatible_model()
    {
        var configuration = new ConfigurationBuilder().Build();
        var environment = new HostingEnvironment
        {
            EnvironmentName = Environments.Development,
            ApplicationName = "SampleProjectManagement.Api"
        };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);

        services.AddSampleAssistant(environment, configuration);
        using var provider = services.BuildServiceProvider();

        Assert.Contains(
            provider.GetServices<INhAiModelProviderFailureReader>(),
            reader => reader is SampleOpenAiFailureReader);
    }

    /// <summary>
    /// The OpenAI client of the sample's real model, with a transport that answers every request with
    /// <paramref name="status"/> and <paramref name="answer"/> instead of calling an endpoint.
    /// </summary>
    private static IChatClient RefusingOpenAiClient(HttpStatusCode status, string answer)
    {
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri("https://models.example.test/v1"),
            Transport = new HttpClientPipelineTransport(new HttpClient(new RefusingEndpoint(status, answer)))
        };
        return new OpenAI.Chat.ChatClient("sample-test-model", new ApiKeyCredential(TestApiKey), options)
            .AsIChatClient();
    }

    private static NhAiChatRequest Request()
    {
        var context = new NhAiInvocationContext(
            "sample-agent",
            "project-assistance",
            new Dictionary<string, string>())
        {
            ActorKind = NhAiActorKind.Agent,
            AccountableOwnerId = "owner-1"
        };
        return new NhAiChatRequest(
            context,
            ProfileName,
            NhAiModelCapability.Chat,
            NhAiDataClassification.Internal,
            [new ChatMessage(ChatRole.User, "How is the roadmap project?")])
        {
            EstimatedInputTokens = 20,
            RequestedOutputTokens = 100
        };
    }

    private sealed class RefusingEndpoint(HttpStatusCode status, string answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(answer, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class CapturedLogs : ILoggerProvider
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

        public IReadOnlyList<string> All => _entries.Select(entry => entry.Message).ToArray();

        public IReadOnlyList<string> Warnings => _entries
            .Where(entry => entry.Level == LogLevel.Warning)
            .Select(entry => entry.Message)
            .ToArray();

        public ILogger CreateLogger(string categoryName)
        {
            return new Logger(_entries);
        }

        public void Dispose()
        {
        }

        private sealed class Logger(ConcurrentQueue<(LogLevel Level, string Message)> entries) : ILogger
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
                entries.Enqueue((logLevel, formatter(state, exception) + (exception is null ? string.Empty : " " + exception)));
            }
        }
    }
}
