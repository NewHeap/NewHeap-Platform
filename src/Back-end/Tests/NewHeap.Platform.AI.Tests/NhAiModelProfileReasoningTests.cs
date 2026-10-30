using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace NewHeap.Platform.AI.Tests;

/// <summary>
/// FB68: a chat profile says how hard its model thinks. The executor applies it to every governed call,
/// with the profile that answers the call, and a caller's own reasoning wins.
/// </summary>
public sealed class NhAiModelProfileReasoningTests
{
    private const string ProfileName = "project-assistant";
    private const string FallbackProfileName = "project-fallback";

    [Fact]
    public async Task Every_call_of_the_profile_carries_its_reasoning()
    {
        var client = new NhAiDeterministicChatClient("answer", "streamed answer");
        using var provider = CreateServices(client, profile => profile.WithReasoning(ReasoningEffort.Low, ReasoningOutput.Summary))
            .BuildServiceProvider();
        using var scope = provider.CreateScope();
        var executor = scope.ServiceProvider.GetRequiredService<INhAiChatExecutor>();

        Assert.True((await executor.GetResponseAsync(Request())).Success);
        var stream = await executor.StartStreamingAsync(Request());
        Assert.True(stream.Success);
        await foreach (var _ in stream.Data.Updates)
        {
        }

        Assert.Equal(2, client.Requests.Count);
        Assert.All(client.Requests, request =>
        {
            Assert.Equal(ReasoningEffort.Low, request.Options!.Reasoning!.Effort);
            Assert.Equal(ReasoningOutput.Summary, request.Options.Reasoning.Output);
        });
    }

    [Fact]
    public async Task A_callers_own_reasoning_wins_and_its_options_stay_as_they_are()
    {
        var client = new NhAiDeterministicChatClient("own", "other");
        using var provider = CreateServices(client, profile => profile.WithReasoning(ReasoningEffort.Low)).BuildServiceProvider();
        using var scope = provider.CreateScope();
        var executor = scope.ServiceProvider.GetRequiredService<INhAiChatExecutor>();
        var own = new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High } };
        var others = new ChatOptions { Temperature = 0.2f };

        Assert.True((await executor.GetResponseAsync(Request() with { Options = own })).Success);
        Assert.True((await executor.GetResponseAsync(Request() with { Options = others })).Success);

        Assert.Equal(ReasoningEffort.High, client.Requests[0].Options!.Reasoning!.Effort);
        Assert.Equal(ReasoningEffort.Low, client.Requests[1].Options!.Reasoning!.Effort);
        Assert.Equal(0.2f, client.Requests[1].Options!.Temperature);
        Assert.Null(others.Reasoning);
    }

    [Fact]
    public async Task A_profile_without_reasoning_leaves_the_call_as_it_was()
    {
        var client = new NhAiDeterministicChatClient("answer");
        using var provider = CreateServices(client, _ => { }).BuildServiceProvider();
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<INhAiChatExecutor>().GetResponseAsync(Request());

        Assert.True(result.Success);
        Assert.Null(Assert.Single(client.Requests).Options);
    }

    [Fact]
    public async Task A_fallback_profile_answers_with_its_own_reasoning()
    {
        var client = new NhAiDeterministicChatClient("answer");
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IChatClient>("fallback-model", client);
        services.AddNewHeapPlatformAI(ai => ai
            .AddChatProfile(ProfileName, profile => profile
                .UseKeyedClient("absent-model")
                .WithReasoning(ReasoningEffort.High)
                .WithFallbackProfiles(FallbackProfileName)
                .WithBudget(2_000, 1_000, 4, 1m))
            .AddChatProfile(FallbackProfileName, profile => profile
                .UseKeyedClient("fallback-model")
                .WithReasoning(ReasoningEffort.Medium)
                .WithBudget(2_000, 1_000, 4, 1m))
            .UseBudgetManager<NhAiTestBudgetManager>());
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<INhAiChatExecutor>().GetResponseAsync(Request());

        Assert.True(result.Success);
        Assert.Equal(FallbackProfileName, result.Data.ProfileName);
        Assert.Equal(ReasoningEffort.Medium, Assert.Single(client.Requests).Options!.Reasoning!.Effort);
    }

    [Fact]
    public void The_profile_shows_its_reasoning_and_two_registrations_must_agree_on_it()
    {
        var services = new ServiceCollection();
        var addTwice = () => services.AddNewHeapPlatformAI(ai => ai
            .AddChatProfile(ProfileName, profile => profile.UseKeyedClient("project-model").WithReasoning(ReasoningEffort.Low))
            .AddChatProfile(ProfileName, profile => profile.UseKeyedClient("project-model").WithReasoning(ReasoningEffort.High)));

        Assert.Throws<InvalidOperationException>(addTwice);

        var registered = new ServiceCollection();
        registered.AddNewHeapPlatformAI(ai => ai.AddChatProfile(
            ProfileName,
            profile => profile.UseKeyedClient("project-model").WithReasoning(ReasoningEffort.None, ReasoningOutput.None)));
        using var provider = registered.BuildServiceProvider();
        Assert.True(provider.GetRequiredService<INhAiModelProfileRegistry>().TryGet(ProfileName, out var found));
        Assert.Equal((ReasoningEffort.None, ReasoningOutput.None), (found.ReasoningEffort, found.ReasoningOutput));
    }

    [Fact]
    public void A_value_outside_the_enum_is_a_programming_error()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentOutOfRangeException>(() => services.AddNewHeapPlatformAI(ai => ai.AddChatProfile(
            ProfileName,
            profile => profile.UseKeyedClient("project-model").WithReasoning((ReasoningEffort)99))));
        Assert.Throws<ArgumentOutOfRangeException>(() => services.AddNewHeapPlatformAI(ai => ai.AddChatProfile(
            ProfileName,
            profile => profile.UseKeyedClient("project-model").WithReasoning(ReasoningEffort.Low, (ReasoningOutput)99))));
    }

    private static ServiceCollection CreateServices(IChatClient client, Action<NhAiModelProfileBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton("project-model", client);
        services.AddNewHeapPlatformAI(ai => ai
            .AddChatProfile(ProfileName, profile =>
            {
                profile
                    .UseKeyedClient("project-model")
                    .WithStreaming(NhAiStreamingPolicy.Allowed)
                    .WithBudget(2_000, 1_000, 4, 1m);
                configure(profile);
            })
            .UseBudgetManager<NhAiTestBudgetManager>());
        return services;
    }

    private static NhAiChatRequest Request()
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
            ProfileName,
            NhAiModelCapability.Chat,
            NhAiDataClassification.Public,
            [new ChatMessage(ChatRole.User, "Which projects are late?")])
        {
            EstimatedInputTokens = 10,
            RequestedOutputTokens = 10
        };
    }
}
