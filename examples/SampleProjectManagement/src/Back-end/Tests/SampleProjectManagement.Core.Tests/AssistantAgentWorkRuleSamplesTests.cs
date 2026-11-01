using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Models;
using SampleProjectManagement.Api.Composition;
using SampleProjectManagement.Core.Services;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// Executable evidence for SPM-387: a rule about runs reads <see cref="NhAiInvocationContext.IsAgentWork"/>.
/// <c>projects.list-members</c> keeps the people on a project out of an agent's own work: it refuses in a
/// run and a consultation and answers in a person's chat, where the actor is the agent as well, so a rule
/// on <see cref="NhAiActorKind"/> would refuse the chat too. A person's own turn in the agent's thread is
/// no agent work either.
/// </summary>
public sealed class AssistantAgentWorkRuleSamplesTests(AssistantCompositionSampleHost host) : IClassFixture<AssistantCompositionSampleHost>
{
    private const string MembersFunction = "projects_list_members_v1";
    private const string Person = "person-387";
    private const string AgentId = "sample-project-planner";
    private const string Purpose = "project-assistance";
    private static readonly Guid DivisionId = Guid.NewGuid();

    [Fact]
    public async Task SPM_387_a_persons_chat_lists_the_people_on_a_project_although_the_agent_is_the_actor()
    {
        var projectId = host.Overview.ProjectId;
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(MembersFunction, new { input = new { projectId } }, "call-members")
            .RespondWithText("Ada and Grace work on the roadmap.");
        host.Model.Use(model);
        using var client = host.CreateClient(Person);
        var conversationId = await host.CreateConversationAsync(client);

        var events = await host.SendAsync(client, conversationId, "Who works on the roadmap project?");

        var completed = Assert.Single(events, evt => evt.Name == "tool.completed").Data;
        Assert.Equal("succeeded", completed.GetProperty("status").GetString());
        var result = JsonSerializer.SerializeToElement(Assert.Single(model.FunctionResults).Result);
        Assert.True(result.GetProperty(NhAiToolResult.SuccessPropertyName).GetBoolean());
        Assert.Equal(2, result.GetProperty("data").GetArrayLength());
    }

    [Fact]
    public async Task SPM_387_a_run_and_a_consultation_are_refused_and_a_persons_turn_in_the_agents_thread_is_not()
    {
        var overview = new FakeProjectOverview();
        await using var provider = CreateProvider();
        await using var scope = provider.CreateAsyncScope();
        var gate = scope.ServiceProvider.GetRequiredService<INhAiToolInvocationGate>();
        var tools = new ServiceCollection()
            .AddSingleton(new ProjectOverviewAiTools(overview))
            .AddSingleton<INhAiToolInvoker>(new NhAiToolInvoker(gate, new NhAiTestBudgetManager()))
            .BuildServiceProvider();
        var catalog = new ProjectOverviewAiToolsNhAiCatalog();
        var listMembers = Assert.Single(catalog.CreateFunctions(tools), function => function.Name == MembersFunction);
        var descriptor = Assert.Single(catalog.Descriptors, tool => tool.Id == ProjectOverviewAiTools.ListMembersToolId);

        // A turn of a run and a consultation, which has no run, as the assistant makes their scopes
        // ambient. The rule refuses both before the tool reads anything, although the person who started
        // them may read the members.
        var run = TurnOf(overview.ProjectId) with { AgentRunId = Guid.NewGuid() };
        var consultation = TurnOf(overview.ProjectId) with { IsRequestTurn = true, RequireApprovalForEffects = true };
        foreach (var agentWork in new[] { run, consultation })
        {
            var refused = await ListAsync(listMembers, agentWork, overview.ProjectId);
            Assert.False(refused.GetProperty(NhAiToolResult.SuccessPropertyName).GetBoolean());
            Assert.Equal(ProjectOverviewAiTools.MembersForPeopleCode, refused.GetProperty(NhAiToolResult.CodePropertyName).GetString());
        }

        Assert.Empty(overview.Reads);

        // A person's own turn in the thread: the same actor and scope, but the person asks, so it answers.
        var personsTurn = TurnOf(overview.ProjectId) with { IsRequestTurn = true, IsAgentWork = false };
        var answered = await ListAsync(listMembers, personsTurn, overview.ProjectId);
        Assert.True(answered.GetProperty(NhAiToolResult.SuccessPropertyName).GetBoolean());
        Assert.Equal(ProjectOverviewAiTools.ListMembersToolId, Assert.Single(overview.Reads).ToolId);

        // The actor kind cannot tell these apart: it is the agent in all three.
        foreach (var turn in new[] { run, consultation, personsTurn })
        {
            using (NhAiAgentInvocationScopes.Begin(turn))
            {
                var context = (await gate.AuthorizeAsync(descriptor, TestContext.Current.CancellationToken)).Data!;
                Assert.Equal(NhAiActorKind.Agent, context.ActorKind);
                Assert.Equal(turn.IsAgentWork, context.IsAgentWork);
            }
        }
    }

    private static async Task<JsonElement> ListAsync(AIFunction listMembers, NhAiAgentInvocationScope turn, Guid projectId)
    {
        using (NhAiAgentInvocationScopes.Begin(turn))
        {
            var output = await listMembers.InvokeAsync(
                new AIFunctionArguments { ["input"] = new ProjectAiProjectInput(projectId) },
                TestContext.Current.CancellationToken);
            return Assert.IsType<JsonElement>(output);
        }
    }

    /// <summary>
    /// The planner on a project in a turn a person started, with that person's rights.
    /// </summary>
    private static NhAiAgentInvocationScope TurnOf(Guid projectId)
    {
        var project = new NhAiSubjectInfo(ProjectSubjects.For(projectId), NhAiSubjectInfo.CreateScopeKey(null, DivisionId))
        {
            DivisionId = DivisionId,
            DisplayName = "Roadmap"
        };
        return new NhAiAgentInvocationScope(AgentId, "assistant-agent:" + AgentId, 1, project, Person, Guid.NewGuid().ToString(), Purpose)
        {
            Accountability = NhAiAgentAccountability.SubjectResponsible,
            InitiatorActorId = Person,
            DelegateFromInitiator = true
        };
    }

    private static ServiceProvider CreateProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor());
        services.AddAuthorization(options =>
            options.AddPolicy(SampleAssistantComposition.AccessPolicy, policy => policy.RequireAuthenticatedUser()));
        services.AddSingleton<INhAiPrincipalFactory, SamplePerson>();
        services.AddNewHeapPlatformAIAspNet(ai => ai
            .UseToolInvocationPurpose(Purpose)
            .AddActiveDivisionScope(SampleAssistantComposition.AccessPolicy)
            .AddCapabilityGrant(ProjectAiTools.ReadCapability, SampleAssistantComposition.AccessPolicy));
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// The person who started the turns, rebuilt for every decision.
    /// </summary>
    private sealed class SamplePerson : INhAiPrincipalFactory
    {
        public ValueTask<ClaimsPrincipal?> CreateAsync(
            string actorId,
            NhAiSubjectInfo subject,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<ClaimsPrincipal?>(new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, actorId)],
                "sample-person")));
        }
    }
}
