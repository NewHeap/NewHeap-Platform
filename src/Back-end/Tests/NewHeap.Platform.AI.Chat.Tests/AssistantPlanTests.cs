using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Live;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NSubstitute;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// The live plan of agent work (<c>run.KeepPlan()</c>): an agent that keeps a plan is offered
/// <c>update_plan</c>, the turn's message keeps the plan, each update streams as <c>plan.updated</c> to
/// the request and the live updates, a turn that waited for a person goes on with its plan, no later
/// turn reads it, a run's result shows its last plan, and an agent created in the administration keeps
/// one when its run settings say so. Off by default. Proved on both relational providers.
/// </summary>
public sealed class AssistantPlanTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string PlanningAgent = "project-assistant";
    private const string PlainAgent = "plain-assistant";
    private const string RunAgent = "project-reviewer";
    private const string QuietAgent = "project-observer";
    private const string Admin = "admin-1";

    private static readonly object FirstPlan = new
    {
        steps = new[]
        {
            new { text = "Find the open projects", status = "in_progress" },
            new { text = "Summarize their status", status = "pending" }
        }
    };

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_chat_turn_keeps_its_plan_in_its_message_and_streams_every_update(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("update_plan", FirstPlan, "call-plan")
            .RespondWithFunctionCall("update_plan", new { advance = true }, "call-advance")
            .RespondWithText("Two projects are open.")
            .RespondWithText("Hello.");
        var live = new CapturedLiveUpdates();
        await using var host = await AssistantTestHost.CreateAsync(
            database,
            provider,
            model,
            configure: services => services.Replace(ServiceDescriptor.Singleton<INhAssistantLiveUpdateTransport>(live)),
            assistantBuilder: assistant => assistant
                .AddAgent(AssistantTestData.Agent(PlainAgent) with { ProfileName = "project-chat" })
                .ConfigureAgent(PlanningAgent, agent => agent.KeepPlan()));
        var conversation = await host.CreateConversationAsync(agentId: PlanningAgent);

        var turn = await host.SendAsync(conversation.Id, "Which projects are open?");

        Assert.Contains("update_plan", ToolNames(model.Requests[0]));
        var updates = turn.Events.OfType<NhAssistantPlanUpdatedEvent>().ToArray();
        Assert.Equal(2, updates.Length);
        Assert.Equal(
            [("Find the open projects", NhAssistantPlanStepStatuses.InProgress), ("Summarize their status", NhAssistantPlanStepStatuses.Pending)],
            updates[0].Plan.Steps.Select(step => (step.Text, step.Status)));
        Assert.Equal(
            [NhAssistantPlanStepStatuses.Completed, NhAssistantPlanStepStatuses.InProgress],
            updates[1].Plan.Steps.Select(step => step.Status));
        var started = turn.Single<NhAssistantTurnStartedEvent>();
        Assert.All(updates, update => Assert.Equal((started.TurnId, started.AssistantMessageId), (update.TurnId, update.MessageId)));
        // The plan shows as the plan, never as tool calls, and each update counts as a call of the turn.
        Assert.DoesNotContain(turn.Events, evt => evt is NhAssistantToolStartedEvent or NhAssistantToolCompletedEvent);
        Assert.Equal(2, turn.Single<NhAssistantTurnCompletedEvent>().Usage.ToolCalls);
        var result = JsonSerializer.SerializeToElement(model.Requests[2].Messages
            .SelectMany(message => message.Contents)
            .OfType<FunctionResultContent>()
            .Single(content => content.CallId == "call-advance")
            .Result);
        Assert.Equal((true, "in_progress"), (result.GetProperty("success").GetBoolean(), result.GetProperty("steps")[1].GetProperty("status").GetString()));

        // A reloaded conversation shows the last plan of the turn in its message, in place.
        var view = await host.ReadViewAsync(conversation.Id);
        var message = view.Messages.Single(item => item.Id == started.AssistantMessageId);
        var plan = Assert.IsType<NhAssistantPlanPartView>(message.Parts[0]);
        Assert.Equal(updates[1].Plan.Steps, plan.Steps);
        Assert.DoesNotContain(message.Parts, part => part is NhAssistantToolCallPartView);

        // The other sessions of the conversation's people received the same snapshots.
        var shared = live.TurnEvents.OfType<NhAssistantPlanUpdatedEvent>().ToArray();
        Assert.Equal(2, shared.Length);
        Assert.Equal(updates[1].Plan.Steps, shared[^1].Plan.Steps);

        // An agent that does not keep a plan is not offered update_plan.
        var plain = await host.CreateConversationAsync(agentId: PlainAgent);
        await host.SendAsync(plain.Id, "Hello?");
        Assert.DoesNotContain("update_plan", ToolNames(model.Requests[^1]));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_turn_that_waited_for_an_answer_goes_on_with_its_plan_and_no_later_turn_reads_it(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("update_plan", FirstPlan, "call-plan")
            .RespondWithFunctionCall("ask_question", new { question = "Which projects count?", choices = new[] { "All", "Mine" } }, "call-ask")
            .RespondWithFunctionCall("update_plan", new { advance = true }, "call-advance")
            .RespondWithText("All open projects are listed.")
            .RespondWithText("Next question answered.");
        await using var host = await AssistantTestHost.CreateAsync(
            database,
            provider,
            model,
            assistantBuilder: assistant => assistant.ConfigureAgent(PlanningAgent, agent => agent.KeepPlan().AllowQuestions()));
        var conversation = await host.CreateConversationAsync(agentId: PlanningAgent);
        var asked = (await host.SendAsync(conversation.Id, "List the open projects.")).Single<NhAssistantQuestionAskedEvent>().Question;

        var answered = await host.AnswerAsync(conversation.Id, asked.QuestionId, 0);

        // The continued part read its plan back: advancing completed the first step.
        var advanced = answered.Single<NhAssistantPlanUpdatedEvent>();
        Assert.Equal([NhAssistantPlanStepStatuses.Completed, NhAssistantPlanStepStatuses.InProgress], advanced.Plan.Steps.Select(step => step.Status));
        var plans = (await host.ReadViewAsync(conversation.Id)).Messages
            .SelectMany(message => message.Parts)
            .OfType<NhAssistantPlanPartView>()
            .ToArray();
        Assert.Equal(advanced.Plan.Steps, plans[^1].Steps);

        // The plan is the agent's statement in its own turn: a later turn's model never reads it.
        await host.SendAsync(conversation.Id, "Anything else?");
        var later = model.Requests[^1];
        Assert.DoesNotContain(later.Messages, item => item.Text.Contains("Find the open projects", StringComparison.Ordinal));
        Assert.DoesNotContain(later.Messages, item => item.Text.Contains("assistant.plan", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_runs_result_shows_the_last_plan_its_agent_kept(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("update_plan", FirstPlan, "call-plan")
            .RespondWithFunctionCall("update_plan", new { advance = true }, "call-advance")
            .RespondWithFunctionCall("update_plan", new { advance = true }, "call-finish")
            .RespondWithText("Reviewed.")
            .RespondWithText("Observed.");
        await using var app = await AssistantWebApplication.StartAsync(
            database,
            model,
            configure: services =>
            {
                services.AddSingleton<RecordingOperations>();
                services.AddSingleton(provider => provider.GetRequiredService<RecordingOperations>().Service);
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, TestPrincipalFactory>());
                // Runs are executed here, not by background operations.
                services.Remove(services.Single(descriptor => descriptor.ImplementationType == typeof(NhAssistantStartupValidator)));
            },
            assistantBuilder: assistant => assistant
                .AddAgent(AssistantTestData.Agent(RunAgent))
                .AddAgent(AssistantTestData.Agent(QuietAgent))
                .ConfigureAgent(RunAgent, run => run.WorksOnApplication().KeepPlan())
                .ConfigureAgent(QuietAgent, run => run.WorksOnApplication()),
            provider: provider);
        var planned = await StartNowAsync(app, RunAgent);
        await ExecuteRunAsync(app, planned);
        var quiet = await StartNowAsync(app, QuietAgent);
        await ExecuteRunAsync(app, quiet);
        using var admin = app.CreateClient(Admin, admin: true);

        var result = await ReadJsonAsync(await admin.GetAsync($"/api/assistant/runs/{planned}"));
        var unplanned = await ReadJsonAsync(await admin.GetAsync($"/api/assistant/runs/{quiet}"));

        var plan = result.GetProperty("plan");
        Assert.Equal(
            [("Find the open projects", "completed"), ("Summarize their status", "completed")],
            plan.GetProperty("steps").EnumerateArray().Select(step => (step.GetProperty("text").GetString(), step.GetProperty("status").GetString())));
        Assert.True(plan.TryGetProperty("updatedAt", out _));
        Assert.Equal(JsonValueKind.Null, unplanned.GetProperty("plan").ValueKind);
        // The run's thread keeps the plan in the run's message, as a chat does, naming the run.
        var conversationId = result.GetProperty("run").GetProperty("conversationId").GetGuid();
        await using var context = app.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        var parts = await context.Messages
            .Where(message => message.ConversationId == conversationId)
            .Select(message => message.PartsJson)
            .ToListAsync();
        var part = Assert.Single(parts.SelectMany(NhAssistantContent.DeserializeParts), item => item.Type == NhAssistantStoredPart.PlanType);
        Assert.Equal(planned, part.RunId);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task An_agent_created_in_the_administration_keeps_a_plan_when_its_run_settings_say_so(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithText("Without a plan.")
            .RespondWithText("With a plan.");
        await using var host = await AssistantTestHost.CreateAsync(
            database,
            provider,
            model,
            configure: services =>
            {
                services.AddSingleton(new TestSubjects());
                services.AddSingleton<RecordingOperations>();
                services.AddSingleton(provider => provider.GetRequiredService<RecordingOperations>().Service);
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, TestPrincipalFactory>());
            },
            assistantBuilder: assistant => assistant
                .AddSubjectType<TestSubjectResolver>()
                .ConfigureAgent(PlanningAgent, agent => agent.KeepPlan()));
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var created = await scope.ServiceProvider.GetRequiredService<NhAssistantAgentAdministration>().CreateAsync(
                new NhAssistantAgentInput(
                    "project-planner",
                    "Project planner",
                    "Plans project work.",
                    "Plan the work.",
                    ["projects.*"],
                    [],
                    null,
                    NhAiAutonomyLevel.Observe,
                    ProfileName: "project-chat"),
                Admin,
                CancellationToken.None);
            Assert.True(created.Success);
        }

        var off = await ConfigureAsync(host, configuration => configuration.SaveRunSettingsAsync(
            "project-planner",
            new NhAssistantAgentRunSettingsRequest(NhAssistantAccountabilities.Agent),
            Admin));
        var chat = await host.CreateConversationAsync(agentId: "project-planner");
        await host.SendAsync(chat.Id, "Plan the launch.");
        var withoutPlan = ToolNames(model.Requests[^1]);
        var on = await ConfigureAsync(host, configuration => configuration.SaveRunSettingsAsync(
            "project-planner",
            new NhAssistantAgentRunSettingsRequest(NhAssistantAccountabilities.Agent, ExpectedVersion: 1) { KeepsPlan = true },
            Admin));
        await host.SendAsync(chat.Id, "Plan the launch again.");

        Assert.False(off.Data!.KeepsPlan);
        Assert.True(on.Data!.KeepsPlan);
        Assert.DoesNotContain("update_plan", withoutPlan);
        Assert.Contains("update_plan", ToolNames(model.Requests[^1]));
        var stored = (await ConfigureAsync(host, configuration => configuration.GetAsync("project-planner")))!;
        Assert.True(stored.RunSettings!.KeepsPlan);
        Assert.True(host.Services.GetRequiredService<NhAssistantRegistrationState>().AgentRuns["project-planner"].KeepsPlan);
        // An agent in code shows what its code says, read-only.
        var code = (await ConfigureAsync(host, configuration => configuration.GetAsync(PlanningAgent)))!;
        Assert.True(code.CodeRunSettings!.KeepsPlan);
    }

    private static async Task<Guid> StartNowAsync(AssistantWebApplication app, string agentId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var receipt = await scope.ServiceProvider.GetRequiredService<NhAssistantAgentTriggers>()
            .StartNowAsync(new NhAssistantStartClearance(agentId, NhAiSystemSubjects.Global, Admin), "Review the projects.", CancellationToken.None);
        Assert.Equal(NhAssistantTriggerOutcomes.Started, receipt.Outcome);
        return receipt.RunId!.Value;
    }

    /// <summary>
    /// Executes the run's background operation once, as the background-operation worker does.
    /// </summary>
    private static async Task ExecuteRunAsync(AssistantWebApplication app, Guid runId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>().HttpContext = null;
        var operation = ActivatorUtilities.CreateInstance<NhAssistantAgentRunOperation>(scope.ServiceProvider);
        var result = await operation.ExecuteAsync(
            new NhAssistantAgentRunRequest(runId),
            Substitute.For<INhBackgroundOperationContext>(),
            CancellationToken.None);
        Assert.True(result.Success);
    }

    private static async Task<T> ConfigureAsync<T>(AssistantTestHost host, Func<INhAssistantAgentConfiguration, Task<T>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<INhAssistantAgentConfiguration>());
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static string[] ToolNames(NhAiRecordedChatRequest request)
    {
        return request.Options?.Tools?.Select(tool => tool.Name).ToArray() ?? [];
    }

    /// <summary>
    /// Records the turn events the live updates would send instead of sending them.
    /// </summary>
    private sealed class CapturedLiveUpdates : INhAssistantLiveUpdateTransport
    {
        private readonly ConcurrentQueue<NhAssistantLiveEvent> _events = new();

        public IReadOnlyList<NhAssistantTurnEvent> TurnEvents => _events.OfType<NhAssistantLiveTurnEvent>().Select(evt => evt.Event).ToArray();

        public Task SendAsync(IReadOnlyCollection<string> actorIds, NhAssistantLiveEvent evt, CancellationToken cancellationToken)
        {
            _events.Enqueue(evt);
            return Task.CompletedTask;
        }
    }
}
