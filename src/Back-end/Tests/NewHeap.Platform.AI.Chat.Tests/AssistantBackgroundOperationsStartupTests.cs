using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// When host start demands the background operations of agent runs (<c>AddAssistantAgentRuns</c>): only
/// for an agent in code that can run, because it works on an object type the host registers or on the
/// whole application, or because a trigger starts it. Chat settings such as questions and a plan work
/// in people's chats and need none, so an assistant that is only used for chats starts without them.
/// </summary>
public sealed class AssistantBackgroundOperationsStartupTests(AssistantDatabaseFixture database)
    : IClassFixture<AssistantDatabaseFixture>
{
    private const string Message =
        "Agents configured with ConfigureAgent need background operations. Call AddAssistantAgentRuns inside WithBackgroundOperations.";

    [Fact]
    public async Task A_host_without_object_types_starts_with_questions_and_a_plan_without_background_operations()
    {
        await using var host = await CreateHostAsync(
            withObjectTypes: false,
            run => run.AllowQuestions().KeepPlan());

        await Validator(host).StartAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData(RunningAgent.AllObjectTypesOnAHostWithObjectTypes)]
    [InlineData(RunningAgent.OneObjectType)]
    [InlineData(RunningAgent.WholeApplication)]
    public async Task A_host_whose_agent_can_run_still_needs_background_operations(RunningAgent agent)
    {
        await using var host = agent switch
        {
            // An agent that does not say what it works on works on every registered object type, so an
            // object's page offers a start by hand.
            RunningAgent.AllObjectTypesOnAHostWithObjectTypes =>
                await CreateHostAsync(withObjectTypes: true, run => run.AllowQuestions().KeepPlan()),
            RunningAgent.OneObjectType =>
                await CreateHostAsync(withObjectTypes: true, run => run.WorksOn("project").KeepPlan()),
            _ => await CreateHostAsync(withObjectTypes: false, run => run.WorksOnApplication().AllowQuestions())
        };

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => Validator(host).StartAsync(CancellationToken.None));

        Assert.Equal(Message, refused.Message);
    }

    public enum RunningAgent
    {
        AllObjectTypesOnAHostWithObjectTypes,
        OneObjectType,
        WholeApplication
    }

    private Task<AssistantTestHost> CreateHostAsync(
        bool withObjectTypes,
        Func<NhAssistantAgentRunBuilder, NhAssistantAgentRunBuilder> configure)
    {
        return AssistantTestHost.CreateAsync(
            database,
            AssistantTestProvider.PostgreSql,
            new NhAiScriptedChatClient(),
            configure: services => services.AddSingleton(new TestSubjects()),
            assistantBuilder: assistant =>
            {
                if (withObjectTypes)
                {
                    assistant.AddSubjectType<TestSubjectResolver>();
                }

                assistant.ConfigureAgent("project-assistant", run => configure(run));
            });
    }

    private static NhAssistantStartupValidator Validator(AssistantTestHost host)
    {
        return host.Services.GetServices<IHostedService>().OfType<NhAssistantStartupValidator>().Single();
    }
}
