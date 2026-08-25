using Microsoft.Extensions.AI;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NewHeap.Platform.AI;
using NewHeap.Platform.AI.AgentFramework;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Models;
using SampleProjectManagement.Core.Models.AI;
using SampleProjectManagement.Core.Services;
using SampleProjectManagement.DAL.Entities;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

public sealed class AiModelProfileSamplesTests
{
    [Fact]
    public async Task Named_project_assistant_profile_resolves_a_consumer_owned_chat_client()
    {
        var chatClient = new NhAiDeterministicChatClient("sample-response");
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IChatClient>("project-assistant-model", chatClient);
        services.AddSingleton<IProjectAiMutationService>(new RecordingProjectAiService());
        services.AddSampleProjectManagementAi();
        using var provider = services.BuildServiceProvider();

        foreach (var hostedService in provider.GetServices<IHostedService>())
        {
            await hostedService.StartAsync(CancellationToken.None);
        }

        using var scope = provider.CreateScope();
        var result = await scope.ServiceProvider
            .GetRequiredService<INhAiModelProfileResolver>()
            .ResolveChatAsync(new NhAiModelResolutionRequest(
                "project-assistant",
                NhAiModelCapability.FunctionCalling | NhAiModelCapability.StructuredOutput,
                NhAiDataClassification.Internal,
                "project-assistance",
                "local"));

        Assert.True(result.Success);
        Assert.Same(chatClient, result.Data.Client);
        Assert.Equal("project-assistant", result.Data.Profile.Name);
        Assert.Equal("sample-project-assistant-v1", result.Data.Profile.EvaluationBaselineId);
        Assert.Equal(4_096, result.Data.Profile.Budget.MaxInputTokens);
        Assert.Equal(
            ["profile:project-assistant:selected"],
            result.Data.DecisionTrace);
    }

    [Fact]
    public void Api_composition_resolves_the_aspnet_tool_invocation_gate()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization();
        services.AddNewHeapPlatformAIAspNet(ai =>
            ai.UseToolInvocationPurpose("project-assistance"));
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<INhAiToolInvocationGate>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IAuthorizationService>());
    }

    [Fact]
    public async Task Project_agent_uses_only_discovered_tools_within_its_autonomy()
    {
        var divisionId = Guid.NewGuid();
        var domainService = new RecordingProjectAiService();
        var context = new NhAiInvocationContext(
            "project-agent",
            "project-assistance",
            new Dictionary<string, string>
            {
                [ProjectAiTools.DivisionScopeKey] = divisionId.ToString()
            })
        {
            ActorKind = NhAiActorKind.Agent,
            AccountableOwnerId = "owner-1",
            AgentVersion = "1",
            ModelProfileName = "project-assistant",
            PromptVersion = ProjectAiAssets.ProjectAgentInstructions.Manifest.Version.ToString(),
            PromptHash = ProjectAiAssets.ProjectAgentInstructions.Manifest.ContentHash,
            ContextPolicyId = ProjectAiAssets.ProjectAgentInstructions.Manifest.ContextPolicyId,
            CapabilityGrants = new HashSet<string>(StringComparer.Ordinal)
            {
                ProjectAiTools.ReadCapability
            }
        };
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IChatClient>(
            "project-assistant-model",
            new NhAiDeterministicChatClient("sample-agent-response"));
        services.AddSingleton<IProjectAiReadService>(domainService);
        services.AddSingleton<IProjectAiMutationService>(domainService);
        services.AddScoped<ProjectAiTools>();
        services.AddScoped<INhAiToolInvocationGate>(
            _ => NhAiTestInvocationGate.Authorized(context));
        services.AddSampleProjectManagementAi();
        services.AddNewHeapPlatformAIAgentFramework();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var descriptor = new NhAiAgentDescriptor(
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

        var creation = await scope.ServiceProvider
            .GetRequiredService<INhAiAgentFrameworkAdapter>()
            .CreateAsync(
                new NhAiAgentCreateRequest(
                    descriptor,
                    context,
                    ProjectAiAssets.ProjectAgentInstructions.Content),
                scope.ServiceProvider);
        Assert.True(creation.Success);
        var instance = creation.Data;

        Assert.Equal("project-agent-v1", instance.Agent.Id);
        Assert.Equal("projects.search", Assert.Single(instance.Tools).Id);
        Assert.Equal(64, ProjectAiAssets.ProjectAgentInstructions.Manifest.ContentHash.Length);
        Assert.DoesNotContain(
            ProjectAiAssets.ProjectAgentInstructions.Content,
            ProjectAiAssets.ProjectAgentInstructions.Manifest.ToString(),
            StringComparison.Ordinal);
    }

    private sealed class RecordingProjectAiService :
        IProjectAiReadService,
        IProjectAiMutationService
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
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(
                TaskResult<ProjectAiStatusChangeReport>.Succeeded(
                    new ProjectAiStatusChangeReport(
                        projectId,
                        status,
                        status,
                        true)));
        }

        public Task<ProjectStatus?> GetStatusForAiAsync(
            Guid divisionId,
            Guid projectId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<ProjectStatus?>(ProjectStatus.Active);
        }
    }
}
