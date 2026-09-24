using System.Security.Claims;
using Microsoft.Extensions.AI;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
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
    public async Task Aspnet_context_accepts_explicit_agent_and_operator_issuer_mappings()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization();
        services.AddNewHeapPlatformAIAspNet(ai => ai
            .UseAuthenticatedClaims(
            [
                new NhAiAspNetIssuerClaimMapping(
                    "https://agents.sample",
                    "agent_issuer",
                    "agent_subject",
                    "agent_tenant"),
                new NhAiAspNetIssuerClaimMapping(
                    "https://operators.sample",
                    "operator_issuer",
                    "operator_subject",
                    "operator_tenant")
            ])
            .AddClaimScope(
                "https://agents.sample",
                "agent_scope",
                "workload",
                required: true)
            .AddClaimScope(
                "https://operators.sample",
                "operator_scope",
                "workload",
                required: true));
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var resolver = scope.ServiceProvider
            .GetRequiredService<INhAiAuthenticatedInvocationContextResolver>();
        var agent = CreatePrincipalContext(
            "agent_issuer",
            "https://agents.sample",
            "agent_subject",
            "same-subject",
            "agent_tenant",
            "agent-tenant",
            "agent_scope",
            "automation");
        var consoleOperator = CreatePrincipalContext(
            "operator_issuer",
            "https://operators.sample",
            "operator_subject",
            "same-subject",
            "operator_tenant",
            "operator-tenant",
            "operator_scope",
            "console");

        var agentResult = await resolver.ResolveAsync(
            agent,
            TestContext.Current.CancellationToken);
        var operatorResult = await resolver.ResolveAsync(
            consoleOperator,
            TestContext.Current.CancellationToken);

        Assert.True(agentResult.Success);
        Assert.True(operatorResult.Success);
        Assert.NotEqual(agentResult.Data.ActorId, operatorResult.Data.ActorId);
        Assert.True(agentResult.Data.TryGetScopeValue("workload", out var agentWorkload));
        Assert.Equal("automation", agentWorkload);
        Assert.True(operatorResult.Data.TryGetScopeValue("workload", out var operatorWorkload));
        Assert.Equal("console", operatorWorkload);
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

    private static DefaultHttpContext CreatePrincipalContext(
        string issuerClaimType,
        string issuer,
        string subjectClaimType,
        string subject,
        string tenantClaimType,
        string tenant,
        string scopeClaimType,
        string scopeValue)
    {
        return new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(issuerClaimType, issuer),
                    new Claim(subjectClaimType, subject),
                    new Claim(tenantClaimType, tenant),
                    new Claim(scopeClaimType, scopeValue)
                ],
                "sample"))
        };
    }
}
