using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI;
using NewHeap.Platform.AI.AgentFramework;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Models;
using SampleProjectManagement.Core.Models.AI;
using SampleProjectManagement.Core.Services;
using SampleProjectManagement.DAL.Entities;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// SPM-225: the project agent keeps the history of its session in a chat history provider the
/// application chooses, through <see cref="NhAiAgentCreateRequest.ChatHistoryProviderFactory"/>; the
/// governed client, the published tools and the invocation context stay NewHeap's. A model service that
/// keeps the conversation itself fails the run next to that provider until a deployment whose data may
/// live at the model provider switches off <see cref="NhAiAgentCreateRequest.ThrowOnChatHistoryProviderConflict"/>.
/// </summary>
public sealed class AiAgentHistorySamplesTests
{
    [Fact]
    public async Task The_project_agent_remembers_its_session_through_the_chosen_history_provider()
    {
        var model = new NhAiDeterministicChatClient(
            "Two projects are late.",
            "Hermes is yours.");
        var context = CreateContext();
        await using var provider = CreateServices(model, context).BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var creation = await scope.ServiceProvider
            .GetRequiredService<INhAiAgentFrameworkAdapter>()
            .CreateAsync(
                new NhAiAgentCreateRequest(Descriptor, context, ProjectAiAssets.ProjectAgentInstructions.Content)
                {
                    // The session's messages stay in the session; a host that keeps conversations
                    // stores them with a provider over its own store instead.
                    ChatHistoryProviderFactory = _ => new InMemoryChatHistoryProvider()
                },
                scope.ServiceProvider,
                TestContext.Current.CancellationToken);
        Assert.True(creation.Success);
        var agent = creation.Data.Agent;
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);

        await agent.RunAsync("Which projects are late?", session, cancellationToken: TestContext.Current.CancellationToken);
        var second = await agent.RunAsync("Which of them is mine?", session, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("Hermes is yours.", second.Text);
        // The second model call carried the first exchange, and both crossed the governed executor.
        Assert.Equal(
            ["Which projects are late?", "Two projects are late.", "Which of them is mine?"],
            model.Requests[1].Messages.Select(message => message.Text));
        Assert.Equal(["projects_search_v1"], model.Requests[1].Options?.Tools?.Select(tool => tool.Name));
    }

    [Fact]
    public async Task The_project_agent_follows_a_conversation_the_model_service_keeps()
    {
        // The model service keeps the conversation itself, as the OpenAI Responses API does: each answer
        // carries the id of the conversation it continues.
        var model = new NhAiDeterministicChatClient();
        model.EnqueueResponse(Answer("Two projects are late.", "conversation-1"));
        model.EnqueueResponse(Answer("Two projects are late.", "conversation-2"));
        model.EnqueueResponse(Answer("Hermes is yours.", "conversation-3"));
        var context = CreateContext();
        await using var provider = CreateServices(model, context).BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var adapter = scope.ServiceProvider.GetRequiredService<INhAiAgentFrameworkAdapter>();
        var request = new NhAiAgentCreateRequest(Descriptor, context, ProjectAiAssets.ProjectAgentInstructions.Content)
        {
            ChatHistoryProviderFactory = _ => new InMemoryChatHistoryProvider()
        };

        // By default the agent refuses a service that keeps the conversation next to its own history
        // provider: that history would live at the model provider, outside the application's retention
        // and forgetting.
        var strict = await adapter.CreateAsync(request, scope.ServiceProvider, TestContext.Current.CancellationToken);
        Assert.True(strict.Success);
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            strict.Data.Agent.RunAsync("Which projects are late?", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("ConversationId or ChatHistoryProvider", refusal.Message, StringComparison.Ordinal);

        // A deployment whose conversation data may live at the model provider switches the error off;
        // the session then follows the conversation the service keeps.
        var creation = await adapter.CreateAsync(
            request with { ThrowOnChatHistoryProviderConflict = false },
            scope.ServiceProvider,
            TestContext.Current.CancellationToken);
        Assert.True(creation.Success);
        var agent = creation.Data.Agent;
        var session = Assert.IsType<ChatClientAgentSession>(
            await agent.CreateSessionAsync(TestContext.Current.CancellationToken));

        await agent.RunAsync("Which projects are late?", session, cancellationToken: TestContext.Current.CancellationToken);
        var second = await agent.RunAsync("Which of them is mine?", session, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("Hermes is yours.", second.Text);
        Assert.Equal("conversation-3", session.ConversationId);
        // The second question went alone, with the service's conversation id, through the governed
        // executor and with only the published search tool.
        var sent = model.Requests[^1];
        Assert.Equal(["Which of them is mine?"], sent.Messages.Select(message => message.Text));
        Assert.Equal("conversation-2", sent.Options?.ConversationId);
        Assert.Equal(["projects_search_v1"], sent.Options?.Tools?.Select(tool => tool.Name));
    }

    private static ChatResponse Answer(string text, string conversationId)
    {
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
        {
            ConversationId = conversationId
        };
    }

    private static NhAiInvocationContext CreateContext()
    {
        return new NhAiInvocationContext(
            "project-agent",
            "project-assistance",
            new Dictionary<string, string>
            {
                [ProjectAiTools.DivisionScopeKey] = Guid.NewGuid().ToString()
            })
        {
            ActorKind = NhAiActorKind.Agent,
            AccountableOwnerId = "owner-1",
            AgentVersion = "1",
            ModelProfileName = "project-assistant",
            PromptVersion = ProjectAiAssets.ProjectAgentInstructions.Manifest.Version.ToString(),
            PromptHash = ProjectAiAssets.ProjectAgentInstructions.Manifest.ContentHash,
            ContextPolicyId = ProjectAiAssets.ProjectAgentInstructions.Manifest.ContextPolicyId,
            CapabilityGrants = new HashSet<string>(StringComparer.Ordinal) { ProjectAiTools.ReadCapability }
        };
    }

    private static ServiceCollection CreateServices(IChatClient model, NhAiInvocationContext context)
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IChatClient>("project-assistant-model", model);
        var projects = new NoProjects();
        services.AddSingleton<IProjectAiReadService>(projects);
        services.AddSingleton<IProjectAiMutationService>(projects);
        services.AddScoped<ProjectAiTools>();
        services.AddScoped<INhAiToolInvocationGate>(_ => NhAiTestInvocationGate.Authorized(context));
        services.AddSampleProjectManagementAi();
        services.AddNewHeapPlatformAIAgentFramework();
        return services;
    }

    private static readonly NhAiAgentDescriptor Descriptor = new(
        "project-agent",
        1,
        "Project Agent",
        "Reads authorized projects without mutation autonomy.",
        "project-assistant",
        NhAiModelCapability.FunctionCalling | NhAiModelCapability.StructuredOutput,
        ["projects.*"],
        NhAiAutonomyLevel.Observe,
        new NhAiModelBudget(2_048, 1_024, 4, 0.05m),
        "sample-project-assistant-v1")
    {
        PromptVersion = ProjectAiAssets.ProjectAgentInstructions.Manifest.Version.ToString(),
        PromptHash = ProjectAiAssets.ProjectAgentInstructions.Manifest.ContentHash,
        ContextPolicyId = ProjectAiAssets.ProjectAgentInstructions.Manifest.ContextPolicyId
    };

    private sealed class NoProjects : IProjectAiReadService, IProjectAiMutationService
    {
        public Task<IReadOnlyList<ProjectAiSearchItem>> SearchForAiAsync(
            Guid divisionId,
            string? query,
            int limit,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<ProjectAiSearchItem>>([]);
        }

        public Task<TaskResult<ProjectAiStatusChangeReport>> ChangeStatusForAiAsync(
            Guid divisionId,
            Guid projectId,
            ProjectStatus status,
            ProjectAgentCause? agentCause,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("An observing agent changes no project.");
        }

        public Task<ProjectStatus?> GetStatusForAiAsync(
            Guid divisionId,
            Guid projectId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<ProjectStatus?>(null);
        }
    }
}
