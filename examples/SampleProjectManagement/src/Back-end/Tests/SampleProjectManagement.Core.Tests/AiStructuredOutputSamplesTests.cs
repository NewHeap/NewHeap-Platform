using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI;
using NewHeap.Platform.AI.Test;
using SampleProjectManagement.Core.Services;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// SPM-335: the status digest sends a structured-output schema that a strict provider accepts, because
/// the sample normalizes it itself, and a call that reaches its deadline is the failed result
/// <c>ai-model-timeout</c>.
/// </summary>
public sealed class AiStructuredOutputSamplesTests
{
    private const string DigestJson = """
        {"summary":"One project is at risk.","onTrack":[{"project":"Apollo","reason":"On schedule."}],"atRisk":[{"project":"Hermes","reason":"Two weeks late."}],"highlight":null}
        """;

    [Fact]
    public async Task The_status_digest_sends_a_schema_that_a_strict_provider_accepts()
    {
        // The deterministic model checks the schema as a provider in strict mode does.
        var model = new NhAiDeterministicChatClient(DigestJson) { StrictStructuredOutput = true };
        await using var provider = CreateServices(model).BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var digest = await scope.ServiceProvider
            .GetRequiredService<ProjectAiStatusDigestService>()
            .CreateAsync(Context(), ["Apollo: on schedule", "Hermes: two weeks late"], TestContext.Current.CancellationToken);

        Assert.True(digest.Success);
        Assert.Equal("Apollo", Assert.Single(digest.Data.OnTrack).Project);
        Assert.Equal("Hermes", Assert.Single(digest.Data.AtRisk).Project);
        Assert.Null(digest.Data.Highlight);
        var sent = Assert.Single(model.Requests).Options?.ResponseFormat;
        Assert.Same(ProjectAiStatusDigestService.ResponseFormat, sent);

        // As generated, the at-risk list refers into the on-track list's items and leaves objects open;
        // the sample moved the item type into $defs and closed every object.
        var generated = ChatResponseFormat.ForJsonSchema<ProjectAiStatusDigest>().Schema!.Value;
        Assert.Contains(
            NhAiStrictStructuredOutput.Validate(generated),
            violation => violation is { Rule: NhAiStrictStructuredOutput.ReferenceRule, Path: "#/properties/atRisk/items" });
        var normalized = ProjectAiStatusDigestService.ResponseFormat.Schema!.Value;
        Assert.Empty(NhAiStrictStructuredOutput.Validate(normalized));
        Assert.Equal(
            "#/$defs/onTrack",
            normalized.GetProperty("properties").GetProperty("atRisk").GetProperty("items").GetProperty("$ref").GetString());
    }

    [Fact]
    public async Task The_generated_schema_without_normalization_is_refused_as_a_provider_refuses_it()
    {
        var model = new NhAiDeterministicChatClient(DigestJson) { StrictStructuredOutput = true };

        var refused = await Assert.ThrowsAsync<HttpRequestException>(() => model.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Apollo: on schedule")],
            new ChatOptions { ResponseFormat = ChatResponseFormat.ForJsonSchema<ProjectAiStatusDigest>() },
            TestContext.Current.CancellationToken));

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, refused.StatusCode);
    }

    [Fact]
    public async Task A_digest_past_the_run_deadline_is_the_failed_result_ai_model_timeout()
    {
        var model = new UnansweredChatClient();
        await using var provider = CreateServices(model).BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        // The profile allows thirty seconds; the run's deadline ends the call first. Nothing throws.
        var digest = await scope.ServiceProvider
            .GetRequiredService<ProjectAiStatusDigestService>()
            .CreateAsync(
                Context() with { Deadline = DateTimeOffset.UtcNow.AddSeconds(1) },
                ["Apollo: on schedule"],
                TestContext.Current.CancellationToken);

        Assert.False(digest.Success);
        Assert.Equal(NhAiModelFailureCodes.Timeout, Assert.Single(digest.GetResultItems()).Name);
    }

    private static ServiceCollection CreateServices(IChatClient model)
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IChatClient>("project-assistant-model", model);
        services.AddSampleProjectManagementAi();
        return services;
    }

    private static NhAiInvocationContext Context()
    {
        return new NhAiInvocationContext(
            "project-agent",
            "project-summary",
            new Dictionary<string, string>())
        {
            ActorKind = NhAiActorKind.Agent,
            AccountableOwnerId = "owner-1"
        };
    }

    /// <summary>A model that waits until the call's token is cancelled.</summary>
    private sealed class UnansweredChatClient : IChatClient
    {
        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return null;
        }

        public void Dispose()
        {
        }
    }
}
