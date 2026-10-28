using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// TC8: compositions and the script tool follow the application's switch per turn under the ceiling in
/// code. By default both are off; configuration or the application's own switch turns them on for an
/// agent that allows them; the switch is asked once per feature and turn, so a change applies from the
/// next turn; a call that still arrives while the feature is off is refused with its code; individual
/// tools never change.
/// </summary>
public sealed class AssistantCompositionSwitchTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string AllowingAgent = "project-assistant";
    private const string PlainAgent = "plain-assistant";
    private const string RunAgent = "project-reviewer";
    private const string SearchFunction = "projects_search_v1";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task By_default_compositions_and_the_script_tool_are_off_while_individual_tools_stay_offered(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient().RespondWithText("Nothing to combine.");
        await using var host = await CreateHostAsync(provider, model);
        var conversation = await host.CreateConversationAsync(agentId: AllowingAgent);

        await host.SendAsync(conversation.Id, "Summarize the projects.");

        var offered = Offered(model, 0);
        Assert.Contains(SearchFunction, offered);
        Assert.DoesNotContain(TestCompositionToolCatalog.OverviewFunction, offered);
        Assert.DoesNotContain(TestCompositionMutationCatalog.CloseAllFunction, offered);
        Assert.DoesNotContain(TestCompositionToolCatalog.ScriptFunction, offered);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Configuration_switches_them_on_for_an_agent_that_allows_them_in_code_and_never_beyond_that_ceiling(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithText("With compositions.")
            .RespondWithText("Without.");
        await using var host = await CreateHostAsync(provider, model, configuration: new Dictionary<string, string?>
        {
            ["NewHeap:AI:Assistant:Compositions"] = "true",
            ["NewHeap:AI:Assistant:Scripts"] = "true"
        });

        var allowing = await host.CreateConversationAsync(agentId: AllowingAgent);
        await host.SendAsync(allowing.Id, "Summarize the projects.");
        var plain = await host.CreateConversationAsync(agentId: PlainAgent);
        await host.SendAsync(plain.Id, "Summarize the projects.");

        var withCeiling = Offered(model, 0);
        Assert.Contains(SearchFunction, withCeiling);
        Assert.Contains(TestCompositionToolCatalog.OverviewFunction, withCeiling);
        Assert.Contains(TestCompositionMutationCatalog.CloseAllFunction, withCeiling);
        // The script tool needs no tool selector: the agent's ceiling is its opt-in.
        Assert.Contains(TestCompositionToolCatalog.ScriptFunction, withCeiling);
        var withoutCeiling = Offered(model, 1);
        Assert.Contains(SearchFunction, withoutCeiling);
        Assert.DoesNotContain(TestCompositionToolCatalog.OverviewFunction, withoutCeiling);
        Assert.DoesNotContain(TestCompositionToolCatalog.ScriptFunction, withoutCeiling);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task The_applications_switch_is_asked_once_per_feature_and_turn_and_a_change_applies_from_the_next_turn(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(TestCompositionToolCatalog.OverviewFunction, new { input = new { query = "risks" } }, "call-1")
            .RespondWithText("Two projects, one risk.")
            .RespondWithText("Now without the overview.")
            .RespondWithText("The plain agent.");
        var switchState = new TestCompositionSwitchState
        {
            Answer = context => context.Feature == NhAiCompositionFeature.Compositions
        };
        await using var host = await CreateHostAsync(provider, model, switchState: switchState);
        var conversation = await host.CreateConversationAsync(agentId: AllowingAgent);

        await host.SendAsync(conversation.Id, "Summarize the projects.");

        // Both model calls of the turn offer the composition; the switch was asked once per feature.
        Assert.Contains(TestCompositionToolCatalog.OverviewFunction, Offered(model, 0));
        Assert.Contains(TestCompositionToolCatalog.OverviewFunction, Offered(model, 1));
        Assert.DoesNotContain(TestCompositionToolCatalog.ScriptFunction, Offered(model, 0));
        Assert.Equal([TestCompositionToolCatalog.Overview.Id], host.Services.GetRequiredService<TestCompositionToolRecorder>().Ran);
        var asked = switchState.Asked.ToArray();
        Assert.Equal(2, asked.Length);
        Assert.Equal(
            [NhAiCompositionFeature.Compositions, NhAiCompositionFeature.Scripts],
            asked.Select(context => context.Feature).Order());
        Assert.All(asked, context =>
        {
            Assert.Equal(NhAssistantCompositionChannel.Chat, context.Channel);
            Assert.Equal(AllowingAgent, context.AgentId);
            Assert.Equal(AssistantTestHost.UserId, context.PersonActorId);
        });

        // The application switches compositions off: the next turn no longer offers them.
        switchState.Answer = _ => false;
        await host.SendAsync(conversation.Id, "And now?");
        Assert.DoesNotContain(TestCompositionToolCatalog.OverviewFunction, Offered(model, 2));
        Assert.Contains(SearchFunction, Offered(model, 2));
        Assert.Equal(4, switchState.Asked.Count);

        // An agent that code does not allow compositions or scripts never asks the switch.
        var plain = await host.CreateConversationAsync(agentId: PlainAgent);
        await host.SendAsync(plain.Id, "Summarize the projects.");
        Assert.Equal(4, switchState.Asked.Count);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task An_approved_composition_call_that_arrives_after_the_switch_went_off_is_refused_and_runs_nothing(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(TestCompositionMutationCatalog.CloseAllFunction, new { input = new { reason = "Finished." } }, "call-close")
            .RespondWithText("Closed.");
        var switchState = new TestCompositionSwitchState();
        await using var host = await CreateHostAsync(provider, model, switchState: switchState);
        var conversation = await host.CreateConversationAsync(agentId: AllowingAgent);
        var paused = await host.SendAsync(conversation.Id, "Close the finished projects.");
        var approval = paused.Single<NhAssistantApprovalRequiredEvent>().Approval;

        // The switch goes off while the proposal waits; the decision starts the next turn.
        switchState.Answer = _ => false;
        var decided = await host.DecideAsync(conversation.Id, approval, approve: true);

        Assert.Equal(NhAssistantErrorCodes.CompositionOff, decided.Single<NhAssistantErrorEvent>().Code);
        Assert.Empty(host.Services.GetRequiredService<TestCompositionToolRecorder>().Ran);
        var invocation = (await host.ReadViewAsync(conversation.Id)).Messages
            .SelectMany(message => message.Parts)
            .OfType<NhAssistantToolCallPartView>()
            .Single();
        Assert.Equal(NhAssistantToolCallStatuses.Failed, invocation.Status);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_run_asks_the_switch_for_the_run_channel_without_a_person_and_offers_what_it_answers(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient().RespondWithText("Reviewed.");
        var switchState = new TestCompositionSwitchState
        {
            Answer = context => context.Channel == NhAssistantCompositionChannel.Run
                && context.Feature == NhAiCompositionFeature.Scripts
        };
        await using var host = await CreateHostAsync(provider, model, switchState: switchState, withRuns: true);

        var turn = await RunTriggeredTurnAsync(host, Guid.NewGuid());

        Assert.Equal(NhAssistantTurnStatuses.Completed, turn.Outcome);
        var offered = Offered(model, 0);
        Assert.Contains(TestCompositionToolCatalog.ScriptFunction, offered);
        Assert.DoesNotContain(TestCompositionToolCatalog.OverviewFunction, offered);
        Assert.Contains(SearchFunction, offered);
        Assert.All(switchState.Asked, context =>
        {
            Assert.Equal(NhAssistantCompositionChannel.Run, context.Channel);
            Assert.Equal(RunAgent, context.AgentId);
            // A run that a trigger started is for nobody in particular.
            Assert.Null(context.PersonActorId);
        });
        Assert.Equal(2, switchState.Asked.Count);
    }

    private static IReadOnlyList<string> Offered(NhAiScriptedChatClient model, int request)
    {
        return (model.Requests[request].Options?.Tools ?? []).Select(tool => tool.Name).ToArray();
    }

    private static async Task<NhAssistantAgentTurnResult> RunTriggeredTurnAsync(AssistantTestHost host, Guid projectId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var receipt = Assert.Single(await services.GetRequiredService<INhAssistantAgentTriggers>()
            .PublishAsync(new ProjectChanged(Guid.NewGuid(), projectId, "created")));
        var runs = services.GetRequiredService<NhAssistantRunStore>();
        var thread = (await runs.FindThreadByConversationAsync(receipt.ConversationId!.Value, CancellationToken.None))!;
        var run = (await runs.FindRunAsync(receipt.RunId!.Value, CancellationToken.None))!;
        var subject = (await services.GetRequiredService<NhAssistantSubjectAccessService>()
            .DescribeAsync(new NhAiSubjectRef(thread.SubjectType, thread.SubjectId), CancellationToken.None))!;
        var options = services.GetRequiredService<NhAssistantRegistrationState>().AgentRuns[RunAgent];
        return await services.GetRequiredService<INhAssistantTurnRunner>().RunAgentTurnAsync(
            new NhAssistantAgentTurnRequest(
                new NhAssistantAgentTurnContext(thread, subject, options, run.Id, run.TriggerId, null, thread.AccountableOwnerId, IsRequestTurn: false),
                [new NhAssistantAgentTurnInput(Guid.NewGuid(), null, run.TriggerId, "Review the project.")]),
            CancellationToken.None);
    }

    private async Task<AssistantTestHost> CreateHostAsync(
        AssistantTestProvider provider,
        NhAiScriptedChatClient model,
        IReadOnlyDictionary<string, string?>? configuration = null,
        TestCompositionSwitchState? switchState = null,
        bool withRuns = false)
    {
        return await AssistantTestHost.CreateAsync(
            database,
            provider,
            model,
            configure: services =>
            {
                services.AddSingleton<TestCompositionToolRecorder>();
                services.AddNewHeapPlatformAI(ai => ai
                    .AddGeneratedToolCatalog<TestCompositionToolCatalog>()
                    .AddGeneratedToolCatalog<TestCompositionMutationCatalog>());
                if (configuration is not null)
                {
                    services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
                        .AddInMemoryCollection(new Dictionary<string, string?>(configuration)
                        {
                            ["NewHeap:AI:Assistant:Enabled"] = "true"
                        })
                        .Build());
                }
                if (switchState is not null)
                {
                    services.AddSingleton(switchState);
                }
                if (withRuns)
                {
                    services.AddSingleton(new TestSubjects());
                    services.AddSingleton<RecordingOperations>();
                    services.AddSingleton(provider => provider.GetRequiredService<RecordingOperations>().Service);
                    services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, TestPrincipalFactory>());
                }
            },
            assistantBuilder: assistant =>
            {
                assistant
                    .AddAgent(AssistantTestData.Agent(PlainAgent))
                    .ConfigureAgentContext(AllowingAgent, context => context.AllowCompositions().AllowScripts());
                if (switchState is not null)
                {
                    assistant.UseCompositionSwitch<TestCompositionSwitch>();
                }
                if (withRuns)
                {
                    assistant
                        .AddAgent(AssistantTestData.Agent(RunAgent))
                        .AddSubjectType<TestSubjectResolver>()
                        .ConfigureAgent(RunAgent, agent => agent.Grant("projects.*", "scripting.*"))
                        .ConfigureAgentContext(RunAgent, context => context.AllowCompositions().AllowScripts())
                        .AddTrigger<ProjectChanged>("project-changed", trigger => trigger
                            .StartAgent(RunAgent)
                            .Subject(evt => new NhAiSubjectRef("project", evt.ProjectId.ToString("N")))
                            .Message(evt => "The project changed: " + evt.Change)
                            .Dedupe(evt => evt.EventId.ToString("N")));
                }
            });
    }
}
