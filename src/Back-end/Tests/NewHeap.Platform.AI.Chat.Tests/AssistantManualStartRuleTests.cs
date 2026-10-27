using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// One start rule for every run a person asks for (K9): a message to a busy agent thread, a restart
/// of a failed run and the generated request tool create a run only for someone who may start the
/// agent by hand, as <c>POST threads/start</c> decides, on both relational providers. The whole
/// application's approve policy gives no start, the agent's required policy and run policy apply,
/// and only administrators start an agent in shadow.
/// </summary>
public sealed class AssistantManualStartRuleTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string ProjectAgent = "project-assistant";
    private const string ManagerAgent = "project-manager";
    private const string Statistics = "weekly-statistics";
    private const string ViewedStatistics = "viewed-statistics";
    private const string ApprovePolicy = "app.system.approve";
    private const string RunPolicy = "app.statistics.run";
    private const string ViewPolicy = "app.statistics.view";
    private const string Admin = "admin-1";
    private const string Approver = "approver-1";
    private const string Lead = "lead-1";
    private const string Viewer = "viewer-1";
    private const string Member = "member-1";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_message_to_a_busy_agent_thread_starts_a_run_only_for_someone_who_may_start_the_agent(AssistantTestProvider provider)
    {
        await using var app = await StartAsync(provider);
        using var approver = app.CreateClient(Approver);
        using var lead = app.CreateClient(Lead);

        // The whole application: the approver types in the thread but may not run the agent, as
        // threads/start says.
        var started = await StartAgentAsync(lead, Statistics, NhAiSubjectRef.SystemType, NhAiSystemSubjects.GlobalId);
        var conversationId = started.GetProperty("conversationId").GetGuid();
        await CompleteAsync(app, started.GetProperty("runId").GetGuid());
        await HoldThreadAsync(app, conversationId, Statistics);
        await AssertErrorAsync(
            await approver.PostAsJsonAsync("/api/assistant/threads/start", new { agentId = Statistics, subjectType = NhAiSubjectRef.SystemType, subjectId = NhAiSystemSubjects.GlobalId }),
            HttpStatusCode.Forbidden,
            NhAssistantErrorCodes.AgentForbidden);

        // Their message finds the thread busy, as a busy chat does; no run and no waiting input is theirs.
        await AssertErrorAsync(
            await approver.PostAsJsonAsync($"/api/assistant/conversations/{conversationId}/messages", new { text = "Export every figure now." }),
            HttpStatusCode.Conflict,
            NhAssistantErrorCodes.ConversationBusy);
        await AssertNothingOfAsync(app, conversationId, Approver);

        // Someone who may start the agent leaves the message for a new run.
        using (var queued = await lead.PostAsJsonAsync($"/api/assistant/conversations/{conversationId}/messages", new { text = "Count the open projects too." }))
        {
            Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
            var receipt = await queued.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(NhAssistantTriggerIds.QueuedMessage, receipt.GetProperty("triggerId").GetString());
            Assert.Equal(NhAssistantTriggerOutcomes.Started, receipt.GetProperty("outcome").GetString());
        }
        await using (var context = Context(app))
        {
            Assert.Equal(
                [NhAssistantTriggerIds.Manual, NhAssistantTriggerIds.QueuedMessage],
                await context.Runs.Where(run => run.ConversationId == conversationId).OrderBy(run => run.CreatedAt).Select(run => run.TriggerId).ToListAsync());
        }

        // An object agent in shadow: its live thread stays visible to the participant, who may not
        // start it while it is in shadow, so a message to the busy thread starts no shadow run.
        using var member = app.CreateClient(Member);
        var project = Guid.NewGuid().ToString("N");
        var onProject = await StartAgentAsync(member, ProjectAgent, "project", project);
        var liveThread = onProject.GetProperty("conversationId").GetGuid();
        await CompleteAsync(app, onProject.GetProperty("runId").GetGuid());
        await using (var scope = app.Services.CreateAsyncScope())
        {
            Assert.True((await scope.ServiceProvider.GetRequiredService<INhAssistantAgentControl>().SetShadowAsync(ProjectAgent, true, Admin)).Success);
        }
        await HoldThreadAsync(app, liveThread, ProjectAgent);
        await AssertErrorAsync(
            await member.PostAsJsonAsync($"/api/assistant/conversations/{liveThread}/messages", new { text = "Export every figure now." }),
            HttpStatusCode.Conflict,
            NhAssistantErrorCodes.ConversationBusy);
        await using (var context = Context(app))
        {
            Assert.False(await context.Threads.AnyAsync(thread => thread.AgentId == ProjectAgent && thread.Shadow));
            Assert.Single(await context.Runs.Where(run => run.ConversationId == liveThread).ToListAsync());
        }
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_restart_of_a_failed_run_follows_the_start_rule(AssistantTestProvider provider)
    {
        await using var app = await StartAsync(provider);
        using var approver = app.CreateClient(Approver);
        using var lead = app.CreateClient(Lead);

        // The whole application: an approver who fails the run policy restarts nothing.
        var started = await StartAgentAsync(lead, Statistics, NhAiSubjectRef.SystemType, NhAiSystemSubjects.GlobalId);
        var conversationId = started.GetProperty("conversationId").GetGuid();
        var failed = started.GetProperty("runId").GetGuid();
        await FailAsync(app, failed);
        var restart = new { mode = "restart" };
        await AssertErrorAsync(
            await approver.PostAsJsonAsync($"/api/assistant/runs/{failed}/retry", restart),
            HttpStatusCode.Forbidden,
            NhAssistantErrorCodes.AgentForbidden);
        await AssertNothingOfAsync(app, conversationId, Approver);

        // Someone who may start the agent restarts it in the same thread.
        using (var restarted = await lead.PostAsJsonAsync($"/api/assistant/runs/{failed}/retry", restart))
        {
            Assert.Equal(HttpStatusCode.Accepted, restarted.StatusCode);
            var receipt = await restarted.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(NhAssistantTriggerOutcomes.Started, receipt.GetProperty("outcome").GetString());
            Assert.Equal(conversationId, receipt.GetProperty("conversationId").GetGuid());
        }
        await using (var context = Context(app))
        {
            Assert.Equal(
                [(NhAssistantRunStatuses.Failed, Lead), (NhAssistantRunStatuses.Queued, Lead)],
                (await context.Runs.Where(run => run.ConversationId == conversationId).OrderBy(run => run.CreatedAt).ToListAsync())
                    .Select(run => (run.Status, run.InitiatorActorId)));
        }

        // A project approver who may not use the agent restarts nothing, nor does one of an agent that
        // is in shadow now.
        var project = Guid.NewGuid().ToString("N");
        var manager = await CreateThreadAsync(app, ManagerAgent, project);
        var managerRun = await AddRunAsync(app, manager, NhAssistantRunStatuses.Failed);
        await AssertErrorAsync(
            await approver.PostAsJsonAsync($"/api/assistant/runs/{managerRun.Id}/retry", restart),
            HttpStatusCode.Forbidden,
            NhAssistantErrorCodes.AgentForbidden);
        var helper = await CreateThreadAsync(app, ProjectAgent, project);
        var helperRun = await AddRunAsync(app, helper, NhAssistantRunStatuses.Cancelled);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            Assert.True((await scope.ServiceProvider.GetRequiredService<INhAssistantAgentControl>().SetShadowAsync(ProjectAgent, true, Admin)).Success);
        }
        await AssertErrorAsync(
            await approver.PostAsJsonAsync($"/api/assistant/runs/{helperRun.Id}/retry", restart),
            HttpStatusCode.Forbidden,
            NhAssistantErrorCodes.AgentForbidden);
        await AssertNothingOfAsync(app, manager.ConversationId, Approver);
        await using (var context = Context(app))
        {
            Assert.False(await context.Threads.AnyAsync(thread => thread.AgentId == ProjectAgent && thread.Shadow));
        }
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task The_request_tool_starts_the_agent_only_for_someone_who_may_start_it(AssistantTestProvider provider)
    {
        await using var app = await StartAsync(provider);
        await using var scope = app.Services.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<NhAssistantConsultationToolHandler>();
        var runOptions = scope.ServiceProvider.GetRequiredService<NhAssistantRegistrationState>().AgentRuns;
        var aboutApplication = runOptions[Statistics].Consultations.Single();
        var viewedApplication = runOptions[ViewedStatistics].Consultations.Single();
        var input = Input(new { subjectId = NhAiSystemSubjects.GlobalId, message = "Export every figure now." });

        // The approver of the whole application fails the agent's run policy; the lead passes it, but
        // not the view policy of the agent whose work only its viewers see.
        var byApprover = await handler.RequestAsync(aboutApplication, input, Person(Approver), CancellationToken.None);
        Assert.Equal(NhAssistantErrorCodes.AgentForbidden, CodeOf(byApprover));
        var unviewed = await handler.RequestAsync(viewedApplication, input, Person(Lead), CancellationToken.None);
        Assert.Equal(NhAssistantErrorCodes.ConsultSubjectUnavailable, CodeOf(unviewed));
        await using (var context = Context(app))
        {
            Assert.False(await context.Runs.AnyAsync());
            Assert.False(await context.QueuedInputs.AnyAsync());
        }
        Assert.Empty(app.Services.GetRequiredService<RecordingOperations>().Enqueued);

        // Who may start the agent by hand starts it through the tool as well.
        var byLead = await handler.RequestAsync(aboutApplication, input, Person(Lead), CancellationToken.None);
        Assert.True(byLead.Success, CodeOf(byLead));
        Assert.Equal(NhAssistantTriggerOutcomes.Started, byLead.Data!.Outcome);
        var byViewer = await handler.RequestAsync(viewedApplication, input, Person(Viewer), CancellationToken.None);
        Assert.True(byViewer.Success, CodeOf(byViewer));
        await using (var context = Context(app))
        {
            Assert.Equal(
                [Lead, Viewer],
                await context.Runs.Where(run => run.TriggerId == NhAssistantTriggerIds.AgentRequest).OrderBy(run => run.CreatedAt).Select(run => run.InitiatorActorId).ToListAsync());
        }
    }

    private Task<AssistantWebApplication> StartAsync(AssistantTestProvider provider)
    {
        return AssistantWebApplication.StartAsync(
            database,
            new NhAiScriptedChatClient(),
            configure: services =>
            {
                services.AddSingleton(new TestSubjects());
                services.AddSingleton<RecordingOperations>();
                services.AddSingleton(provider => provider.GetRequiredService<RecordingOperations>().Service);
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, StartRulePrincipals>());
                services.AddAuthorization(options =>
                {
                    options.AddPolicy(ApprovePolicy, policy => policy.RequireClaim("permission", ApprovePolicy));
                    options.AddPolicy(RunPolicy, policy => policy.RequireClaim("permission", RunPolicy));
                    options.AddPolicy(ViewPolicy, policy => policy.RequireClaim("permission", ViewPolicy));
                });
                // Runs are recorded, not executed: the startup check would require their background operations.
                services.Remove(services.Single(descriptor => descriptor.ImplementationType == typeof(NhAssistantStartupValidator)));
            },
            assistantBuilder: assistant => assistant
                .AddSubjectType<TestSubjectResolver>()
                .UseSystemSubject(system => system.ApprovePolicy(ApprovePolicy))
                .AddAgent(AssistantTestData.Agent(Statistics))
                .AddAgent(AssistantTestData.Agent(ViewedStatistics))
                .ConfigureAgent(ProjectAgent, run => run.WorksOn("project"))
                .ConfigureAgent(ManagerAgent, run => run.WorksOn("project"))
                .ConfigureAgent(Statistics, run => run
                    .WorksOnApplication()
                    .RunPolicy(RunPolicy)
                    .AllowConsultation(NhAiSubjectRef.SystemType, consult => consult
                        .Description("Knows the figures of the whole application.")
                        .Proposals(false)
                        .AllowRequests()))
                .ConfigureAgent(ViewedStatistics, run => run
                    .WorksOnApplication()
                    .RunPolicy(RunPolicy)
                    .ViewPolicy(ViewPolicy)
                    .AllowConsultation(NhAiSubjectRef.SystemType, consult => consult
                        .Description("Knows the figures of the whole application.")
                        .Proposals(false)
                        .AllowRequests())),
            provider: provider);
    }

    private static async Task<JsonElement> StartAgentAsync(HttpClient client, string agentId, string subjectType, string subjectId)
    {
        using var response = await client.PostAsJsonAsync("/api/assistant/threads/start", new { agentId, subjectType, subjectId, message = "Start." });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, $"threads/start answered {response.StatusCode}: {body}");
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// Ends the run as completed, so no queued run is left for a message to join.
    /// </summary>
    private static async Task CompleteAsync(AssistantWebApplication app, Guid runId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<NhAssistantRunStore>().TrySetRunStatusAsync(
            runId,
            [NhAssistantRunStatuses.Queued],
            NhAssistantRunStatuses.Completed,
            CancellationToken.None));
    }

    private static async Task FailAsync(AssistantWebApplication app, Guid runId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<NhAssistantRunStore>().TrySetRunStatusAsync(
            runId,
            [NhAssistantRunStatuses.Queued],
            NhAssistantRunStatuses.Failed,
            CancellationToken.None,
            NhAssistantRunFailureClasses.Internal,
            "test-failure"));
    }

    /// <summary>
    /// Leaves the thread as a working run's turn does: running, held by the agent.
    /// </summary>
    private static async Task HoldThreadAsync(AssistantWebApplication app, Guid conversationId, string agentId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<INhAssistantStore>().TryBeginTurnAsync(
            conversationId,
            NhAssistantAgentEvents.AgentActorPrefix + agentId,
            [NhAssistantConversationStatuses.Idle, NhAssistantConversationStatuses.Failed],
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.AddHours(-1),
            CancellationToken.None));
    }

    private static async Task<AssistantThread> CreateThreadAsync(AssistantWebApplication app, string agentId, string projectId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var agent = (await services.GetRequiredService<NhAssistantAgentCatalog>().FindAsync(agentId, includeDisabled: false, CancellationToken.None))!;
        var info = (await services.GetRequiredService<NhAssistantSubjectAccessService>().DescribeAsync(new NhAiSubjectRef("project", projectId), CancellationToken.None))!;
        return (await services.GetRequiredService<NhAssistantThreadService>().GetOrCreateAsync(agent, info, CancellationToken.None)).Data!;
    }

    private static async Task<AssistantRun> AddRunAsync(AssistantWebApplication app, AssistantThread thread, string status)
    {
        var run = new AssistantRun
        {
            Id = Guid.NewGuid(),
            ConversationId = thread.ConversationId,
            TriggerId = NhAssistantTriggerIds.Manual,
            InitiatorActorId = Member,
            AccountableOwnerId = thread.AccountableOwnerId,
            Status = status,
            AgentVersion = 1,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            ConcurrencyStamp = Guid.NewGuid()
        };
        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<NhAssistantRunStore>().AddRunAsync(run, CancellationToken.None);
        return run;
    }

    /// <summary>
    /// The person started no run in the thread and left no input in it.
    /// </summary>
    private static async Task AssertNothingOfAsync(AssistantWebApplication app, Guid conversationId, string actorId)
    {
        await using var context = Context(app);
        Assert.False(await context.Runs.AnyAsync(run => run.InitiatorActorId == actorId));
        Assert.False(await context.QueuedInputs.AnyAsync(input => input.ConversationId == conversationId
            && (input.AuthorActorId == actorId || input.InitiatorActorId == actorId)));
    }

    private static NhAssistantDbContext Context(AssistantWebApplication app)
    {
        return app.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
    }

    /// <summary>
    /// The person as an MCP client calls the tools: outside a turn, with their own rights.
    /// </summary>
    private static NhAiInvocationContext Person(string actorId)
    {
        return new NhAiInvocationContext(actorId, "project-assistance", new Dictionary<string, string>())
        {
            ActorKind = NhAiActorKind.Human
        };
    }

    private static JsonElement Input(object value)
    {
        return JsonSerializer.SerializeToElement(value);
    }

    private static string? CodeOf(NewHeap.Platform.Common.Models.TaskResult result)
    {
        return result.GetResultItems().Select(item => item.Name).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
    }

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == status, $"{response.RequestMessage?.RequestUri} answered {response.StatusCode}: {body}");
            using var document = JsonDocument.Parse(body);
            Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
        }
    }

    /// <summary>
    /// The people as the whole application's roles and headless runs see them: the approver passes
    /// the approve policy only, the lead also the run policy, the viewer also the view policy, the
    /// administrator the administration policy and the member only uses the assistant.
    /// </summary>
    private sealed class StartRulePrincipals : INhAiPrincipalFactory
    {
        public ValueTask<ClaimsPrincipal?> CreateAsync(string actorId, NhAiSubjectInfo subject, CancellationToken cancellationToken = default)
        {
            var permissions = actorId switch
            {
                Admin => new[] { "assistant", "assistant-admin" },
                Approver => new[] { "assistant", ApprovePolicy },
                Lead => new[] { "assistant", ApprovePolicy, RunPolicy },
                Viewer => new[] { "assistant", ApprovePolicy, RunPolicy, ViewPolicy },
                Member => new[] { "assistant" },
                _ => null
            };
            return ValueTask.FromResult(permissions is null
                ? null
                : new ClaimsPrincipal(new ClaimsIdentity(
                    permissions.Select(value => new Claim("permission", value)).Append(new Claim(ClaimTypes.NameIdentifier, actorId)),
                    "test")));
        }
    }
}
