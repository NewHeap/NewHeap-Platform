using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Models;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// What an agent works on (W2 of the plan for agents working on application objects): the statement
/// in code and as data, its storage on both relational providers, and the one rule that triggers,
/// schedules, manual starts and hand-offs follow.
/// </summary>
[Collection(AssistantDatabaseCollection.Name)]
public sealed class AssistantAgentWorksOnTests(AssistantDatabaseFixture database)
{
    private const string AgentId = "project-assistant";
    private const string Closer = "project-closer";
    private const string Follower = "work-follower";
    private const string Admin = "admin-1";
    private const string Member = "member-1";
    private const string WorkTrigger = "work-changed";
    private const string ProjectTrigger = "project-changed";
    private const string ManagePolicy = "app.project.manage";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Triggers_manual_starts_and_hand_offs_start_an_agent_only_on_the_types_it_works_on(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        var project = new NhAiSubjectRef("project", Guid.NewGuid().ToString("N"));
        var task = new NhAiSubjectRef("task", Guid.NewGuid().ToString("N"));

        // An event whose subject type is known only when it fires reaches the agent for a project only.
        var projectReceipt = Assert.Single(await PublishAsync(host, new WorkItemChanged(Guid.NewGuid(), project)), receipt => receipt.TriggerId == WorkTrigger);
        Assert.Equal(NhAssistantTriggerOutcomes.Started, projectReceipt.Outcome);
        var taskReceipt = Assert.Single(await PublishAsync(host, new WorkItemChanged(Guid.NewGuid(), task)), receipt => receipt.TriggerId == WorkTrigger);
        Assert.Equal((NhAssistantTriggerOutcomes.Rejected, NhAssistantErrorCodes.AgentDoesNotWorkOn, (Guid?)null), (taskReceipt.Outcome, taskReceipt.ReasonCode, taskReceipt.ConversationId));
        var refused = Assert.Single(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AgentTriggerRejected && evt.TriggerId == WorkTrigger);
        Assert.Equal((NhAssistantErrorCodes.AgentDoesNotWorkOn, "task", task.Id), (refused.ResultCode, refused.SubjectType, refused.SubjectId));

        // By hand as well: nothing starts and no thread exists for the task.
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var triggers = scope.ServiceProvider.GetRequiredService<INhAssistantAgentTriggers>();
            var byHand = await triggers.StartAsync(AgentId, task, Member, "Look at this task.");
            Assert.Equal((NhAssistantTriggerOutcomes.Rejected, NhAssistantErrorCodes.AgentDoesNotWorkOn), (byHand.Outcome, byHand.ReasonCode));
            var projectStart = await triggers.StartAsync(AgentId, project, Member, "Look at this project.");
            Assert.Contains(projectStart.Outcome, new[] { NhAssistantTriggerOutcomes.Started, NhAssistantTriggerOutcomes.Queued });
        }
        Assert.Null(await Runs(host).FindThreadAsync(NhAiSubjectInfo.CreateScopeKey(null, null), AgentId, task, CancellationToken.None));

        // A hand-off reaches the target only on what it works on, even when it accepts the type.
        var handOff = await HandOffAsync(host, project, task);
        Assert.Equal((NhAssistantHandOffOutcomes.Refused, NhAssistantErrorCodes.AgentDoesNotWorkOn), (handOff.Outcome, handOff.Code));

        // The rule and the hints read the same statement.
        var rule = host.Services.GetRequiredService<NhAssistantWorksOnRule>();
        Assert.True(rule.Allows(AgentId, "project"));
        Assert.False(rule.Allows(AgentId, "task"));
        Assert.False(rule.Allows(AgentId, NhAiSubjectRef.SystemType));
        Assert.True(rule.Allows("project-chat-only", "task"));
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var known = await scope.ServiceProvider.GetRequiredService<NhAssistantAgentKnownSubjectTypes>()
                .ListAsync([AgentId, Closer], CancellationToken.None);
            // The typed trigger and the thread name projects; the closer accepts hand-offs about tasks.
            Assert.Equal(["project"], known[AgentId]);
            Assert.Equal(["task"], known[Closer]);
        }
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task An_agent_created_in_the_administration_works_on_what_its_administrator_stored(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            Assert.True((await Administration(scope).CreateAsync(Input(null), Admin, CancellationToken.None)).Success);
        }

        // A new agent that says nothing works on all object types, and says so.
        var created = (await FindAgentAsync(host, Follower))!;
        Assert.True(created.WorksOn.IsEquivalentTo(NhAssistantAgentWorksOn.Undeclared with { Declared = true }));

        // Saving checks the statement and answers with field errors.
        Assert.Equal("worksOnSubjectTypes", await FieldOfUpdateAsync(host, Works(types: ["order"])));
        Assert.Equal("worksOnSubjectTypes", await FieldOfUpdateAsync(host, Works(types: [NhAiSubjectRef.SystemType])));
        Assert.Equal("worksOnSubjectTypes", await FieldOfUpdateAsync(host, Works(types: ["Not_A_Type"])));
        Assert.Equal("worksOnSubjectTypes", await FieldOfUpdateAsync(host, Works(types: Enumerable.Range(0, 21).Select(index => $"type-{index}").ToArray())));
        Assert.Equal("worksOn", await FieldOfUpdateAsync(host, Works(types: ["task"], all: true)));
        Assert.Equal("worksOn", await FieldOfUpdateAsync(host, Works(types: [], all: false, application: false)));
        Assert.Equal("perDivision", await FieldOfUpdateAsync(host, Works(types: ["task"], perDivision: true)));
        Assert.Equal("runPolicy", await FieldOfUpdateAsync(host, Works(types: ["task"], runPolicy: "app.unlisted")));
        Assert.Equal("viewPolicy", await FieldOfUpdateAsync(host, Works(types: ["task"], viewPolicy: "app.unlisted")));

        // Tasks and the whole application, with a run and a view policy the application lists.
        var stored = Works(types: [" task ", "task"], application: true, runPolicy: ManagePolicy, viewPolicy: AssistantTestHost.AccessPolicy);
        Assert.True((await UpdateAsync(host, stored, expectedVersion: 1)).Success);
        var updated = (await FindAgentAsync(host, Follower))!;
        Assert.Equal(["task"], updated.WorksOn.SubjectTypes);
        Assert.Equal((false, true, false, ManagePolicy, AssistantTestHost.AccessPolicy, true),
            (updated.WorksOn.AllSubjects, updated.WorksOn.Application, updated.WorksOn.PerDivision, updated.WorksOn.RunPolicy, updated.WorksOn.ViewPolicy, updated.WorksOn.Declared));
        Assert.True(updated.WorksOn.Covers(NhAiSubjectRef.SystemType));
        Assert.False(updated.WorksOn.Covers("project"));

        // An update that says nothing about it keeps it.
        Assert.True((await UpdateAsync(host, null, expectedVersion: 2)).Success);
        Assert.True((await FindAgentAsync(host, Follower))!.WorksOn.IsEquivalentTo(updated.WorksOn));

        // With run settings its runs follow the statement.
        Assert.True((await ConfigureAsync(host, configuration => configuration.SaveRunSettingsAsync(
            Follower,
            new NhAssistantAgentRunSettingsRequest(NhAssistantAccountabilities.ServiceAccount, KnownAccountPrincipalFactory.First, ApprovalPolicy: "project-review"),
            Admin))).Success);
        Assert.True(State(host).AgentRuns[Follower].WorksOn.IsEquivalentTo(updated.WorksOn));

        // A data trigger on an event whose type is known only when it fires: refused for a project.
        Assert.True((await SaveTriggerAsync(host, "follower-work", new NhAssistantDataTriggerRequest(NhAssistantDataTriggerSources.Event, Event: "work.changed"))).Success);
        var projectReceipt = Assert.Single(await PublishAsync(host, new WorkItemChanged(Guid.NewGuid(), new NhAiSubjectRef("project", Guid.NewGuid().ToString("N")))), receipt => receipt.TriggerId == "follower-work");
        Assert.Equal((NhAssistantTriggerOutcomes.Rejected, NhAssistantErrorCodes.AgentDoesNotWorkOn), (projectReceipt.Outcome, projectReceipt.ReasonCode));
        var taskReceipt = Assert.Single(await PublishAsync(host, new WorkItemChanged(Guid.NewGuid(), new NhAiSubjectRef("task", Guid.NewGuid().ToString("N")))), receipt => receipt.TriggerId == "follower-work");
        Assert.Equal(NhAssistantTriggerOutcomes.Started, taskReceipt.Outcome);

        // A data schedule over a mixed set starts the task and records the refused project.
        Assert.True((await SaveTriggerAsync(host, "follower-weekly", new NhAssistantDataTriggerRequest(
            NhAssistantDataTriggerSources.Schedule, Cron: "0 7 * * 1", SubjectSet: "mixed-work", Message: "Weekly check"))).Success);
        IReadOnlyList<NhAssistantTriggerReceipt> ticks;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            ticks = await scope.ServiceProvider.GetRequiredService<NhAssistantScheduledTriggerJob>()
                .RunAsync("follower-weekly", new DateTimeOffset(2026, 10, 19, 5, 0, 0, TimeSpan.Zero), CancellationToken.None);
        }
        Assert.Equal(
            [NhAssistantTriggerOutcomes.Rejected + ":" + NhAssistantErrorCodes.AgentDoesNotWorkOn, NhAssistantTriggerOutcomes.Started + ":"],
            ticks.Select(receipt => receipt.Outcome + ":" + receipt.ReasonCode));
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AgentTriggerRejected
            && evt.TriggerId == "follower-weekly"
            && evt.ResultCode == NhAssistantErrorCodes.AgentDoesNotWorkOn
            && evt.SubjectType == "project");

        // An event that names projects up front does not fit; once the agent works on all objects it does.
        var typed = new NhAssistantDataTriggerRequest(NhAssistantDataTriggerSources.Event, Event: "project.changed");
        Assert.Equal("event", Field(await SaveTriggerAsync(host, "follower-project", typed)));
        Assert.True((await UpdateAsync(host, Works(all: true, application: true), expectedVersion: 3)).Success);
        Assert.True((await SaveTriggerAsync(host, "follower-project", typed)).Success);

        // Narrowing it again leaves the trigger out of effect as data that no longer fits.
        Assert.True((await UpdateAsync(host, Works(types: ["task"]), expectedVersion: 4)).Success);
        var configuration = (await ConfigureAsync(host, service => service.GetAsync(Follower)))!;
        var problem = Assert.Single(configuration.Problems);
        Assert.Equal((NhAssistantRunConfigurationProblems.TriggerKind, "follower-project", NhAssistantRunConfigurationProblems.NotWorkedOn), (problem.Kind, problem.Id, problem.Code));
        Assert.False(State(host).Triggers.ContainsKey("follower-project"));
    }

    [Fact]
    public async Task The_administration_reads_and_writes_what_an_agent_works_on()
    {
        await using var app = await AssistantWebApplication.StartAsync(
            database,
            new NhAiScriptedChatClient(),
            configure: services =>
            {
                services.AddSingleton(new TestSubjects());
                // This host runs no agents; the startup check would require their background operations.
                services.Remove(services.Single(descriptor => descriptor.ImplementationType == typeof(NhAssistantStartupValidator)));
            },
            assistantBuilder: assistant => assistant
                .AddSubjectType<TestSubjectResolver>(type => type.Named(new() { ["en"] = "Project", ["nl"] = "Project" }, fallbackLanguage: "en"))
                .AddSubjectType<WorksOnTaskResolver>()
                .AddAgentPolicy(TestAuthenticationHandler.ManagerPolicy, "People who manage projects.")
                .AddTriggerEvent<ProjectChanged>("project.changed", evt => evt
                    .Description("A project changed.")
                    .Subject("project", change => change.ProjectId.ToString("N"))
                    .Message(change => change.Change))
                .ConfigureAgent(AgentId, agent => agent
                    .WorksOn("project")
                    .RunPolicy(TestAuthenticationHandler.ManagerPolicy)));
        using var admin = app.CreateClient(admin: true);
        using var member = app.CreateClient(Member);

        var catalog = await ReadJsonAsync(await admin.GetAsync("/api/assistant/admin/run-catalog"));
        var policy = Assert.Single(catalog.GetProperty("agentPolicies").EnumerateArray());
        Assert.Equal((TestAuthenticationHandler.ManagerPolicy, "People who manage projects."), (policy.GetProperty("name").GetString(), policy.GetProperty("description").GetString()));
        Assert.False(catalog.GetProperty("divisions").GetBoolean());
        Assert.Equal("project", catalog.GetProperty("triggerEvents")[0].GetProperty("subjectType").GetString());
        Assert.Equal(["nh-system", "project", "task"], catalog.GetProperty("subjectTypes").EnumerateArray().Select(type => type.GetProperty("id").GetString()));

        var input = new Dictionary<string, object?>
        {
            ["id"] = Follower,
            ["displayName"] = "Work follower",
            ["description"] = "Follows up tasks.",
            ["instructions"] = "Follow up.",
            ["toolSelectors"] = new[] { "projects.search" },
            ["mcpServerIds"] = Array.Empty<string>(),
            ["requiredPolicy"] = null,
            ["autonomy"] = "observe",
            ["worksOnSubjectTypes"] = new[] { "task" },
            ["worksOnApplication"] = true,
            ["runPolicy"] = TestAuthenticationHandler.ManagerPolicy
        };
        using var created = await admin.PostAsync("/api/assistant/admin/agents", Json(input));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await ReadJsonAsync(created);
        Assert.Equal(["task"], body.GetProperty("worksOnSubjectTypes").EnumerateArray().Select(type => type.GetString()));
        Assert.Equal((false, true, false, true), (body.GetProperty("worksOnAllSubjects").GetBoolean(), body.GetProperty("worksOnApplication").GetBoolean(), body.GetProperty("perDivision").GetBoolean(), body.GetProperty("worksOnDeclared").GetBoolean()));
        Assert.Equal(TestAuthenticationHandler.ManagerPolicy, body.GetProperty("runPolicy").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("viewPolicy").ValueKind);

        // Errors appear at their fields; without the six fields an update keeps the statement.
        using var unknown = await admin.PutAsync($"/api/assistant/admin/agents/{Follower}", Json(new Dictionary<string, object?>(input)
        {
            ["worksOnSubjectTypes"] = new[] { "order" },
            ["viewPolicy"] = "app.unlisted",
            ["expectedVersion"] = 1
        }));
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        var errors = (await ReadJsonAsync(unknown)).GetProperty("errors");
        Assert.Equal("not-found", errors.GetProperty("worksOnSubjectTypes")[0].GetString());
        Assert.Equal("not-found", errors.GetProperty("viewPolicy")[0].GetString());
        var kept = new Dictionary<string, object?>(input) { ["description"] = "Follows up tasks by rule.", ["expectedVersion"] = 1 };
        foreach (var field in new[] { "worksOnSubjectTypes", "worksOnApplication", "runPolicy" })
        {
            kept.Remove(field);
        }
        using var keptResponse = await admin.PutAsync($"/api/assistant/admin/agents/{Follower}", Json(kept));
        Assert.Equal(HttpStatusCode.OK, keptResponse.StatusCode);
        Assert.True((await ReadJsonAsync(keptResponse)).GetProperty("worksOnApplication").GetBoolean());

        // Agents in code show their statement; one that does not say works on all objects.
        var listed = (await ReadJsonAsync(await admin.GetAsync("/api/assistant/admin/agents"))).EnumerateArray()
            .ToDictionary(agent => agent.GetProperty("id").GetString()!);
        var code = listed[AgentId];
        Assert.Equal(["project"], code.GetProperty("worksOnSubjectTypes").EnumerateArray().Select(type => type.GetString()));
        Assert.Equal((false, true), (code.GetProperty("worksOnAllSubjects").GetBoolean(), code.GetProperty("worksOnDeclared").GetBoolean()));
        Assert.Equal(TestAuthenticationHandler.ManagerPolicy, code.GetProperty("runPolicy").GetString());
        var undeclared = listed["project-manager"];
        Assert.Equal((true, false), (undeclared.GetProperty("worksOnAllSubjects").GetBoolean(), undeclared.GetProperty("worksOnDeclared").GetBoolean()));
        Assert.Empty(undeclared.GetProperty("knownSubjectTypes").EnumerateArray());

        // People cannot start or open the agent about a task, and the lookups say they cannot start it.
        var taskId = Guid.NewGuid().ToString("N");
        using var start = await member.PostAsync("/api/assistant/threads/start", Json(new { agentId = AgentId, subjectType = "task", subjectId = taskId }));
        Assert.Equal(HttpStatusCode.Conflict, start.StatusCode);
        Assert.Equal(NhAssistantErrorCodes.AgentDoesNotWorkOn, (await ReadJsonAsync(start)).GetProperty("code").GetString());
        using var open = await member.PostAsync("/api/assistant/threads/open", Json(new { agentId = AgentId, subjectType = "task", subjectId = taskId }));
        Assert.Equal(HttpStatusCode.Conflict, open.StatusCode);
        Assert.Equal(NhAssistantErrorCodes.AgentDoesNotWorkOn, (await ReadJsonAsync(open)).GetProperty("code").GetString());
        var taskLookup = await ReadJsonAsync(await member.GetAsync($"/api/assistant/threads?agentId={AgentId}&subjectType=task&subjectId={taskId}"));
        Assert.False(taskLookup.GetProperty("canStart").GetBoolean());
        var projectLookup = await ReadJsonAsync(await member.GetAsync($"/api/assistant/threads?agentId={AgentId}&subjectType=project&subjectId={Guid.NewGuid():N}"));
        Assert.True(projectLookup.GetProperty("canStart").GetBoolean());
    }

    [Fact]
    public void The_statement_in_code_names_types_all_objects_or_the_application()
    {
        var undeclared = Configure(_ => { });
        Assert.True(undeclared.IsEquivalentTo(NhAssistantAgentWorksOn.Undeclared));
        Assert.True(undeclared.Covers("anything"));
        Assert.False(undeclared.Covers(NhAiSubjectRef.SystemType));
        Assert.False(undeclared.Covers("nh-other"));

        // Choosing only the application replaces "all object types"; per division and policies keep it.
        var application = Configure(run => run.PerDivision().WorksOnApplication());
        Assert.Equal((false, true, true, true), (application.AllSubjects, application.Application, application.PerDivision, application.Declared));
        Assert.False(application.Covers("project"));
        var policies = Configure(run => run.RunPolicy("app.run").ViewPolicy("app.view"));
        Assert.Equal((true, false, "app.run", "app.view"), (policies.AllSubjects, policies.Declared, policies.RunPolicy, policies.ViewPolicy));

        var types = Configure(run => run.WorksOn("task", "project").WorksOn("project").WorksOnApplication());
        Assert.Equal(["project", "task"], types.SubjectTypes);
        var all = Configure(run => run.WorksOnApplication().WorksOnAllObjects());
        Assert.Equal((true, true), (all.AllSubjects, all.Application));

        Assert.Throws<ArgumentException>(() => Configure(run => run.WorksOn(NhAiSubjectRef.SystemType)));
        Assert.Throws<ArgumentException>(() => Configure(run => run.WorksOn("Not_A_Type")));
        Assert.Throws<ArgumentException>(() => Configure(run => run.WorksOn()));
        Assert.Throws<InvalidOperationException>(() => Configure(run => run.WorksOn("project").WorksOnAllObjects()));
        Assert.Throws<InvalidOperationException>(() => Configure(run => run.WorksOnAllObjects().WorksOn("project")));
        Assert.Throws<InvalidOperationException>(() => Configure(run => run.WorksOn(Enumerable.Range(0, 21).Select(index => $"type-{index}").ToArray())));
        Assert.Throws<ArgumentException>(() => Configure(run => run.RunPolicy(" app.run")));
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddNewHeapAssistant(assistant => assistant
            .AddTrigger<WorkItemChanged>("probe", trigger => trigger.Subject(NhAiSubjectRef.SystemType, _ => "global"))));
    }

    [Fact]
    public void Startup_refuses_triggers_hand_offs_and_consultations_on_types_the_agent_does_not_work_on()
    {
        var typedTrigger = Assert.Throws<InvalidOperationException>(() => Validate(assistant => assistant
            .ConfigureAgent(AgentId, run => run.WorksOn("project"))
            .AddTrigger<WorkItemChanged>("task-trigger", trigger => trigger
                .StartAgent(AgentId)
                .Subject("task", change => change.Subject.Id)
                .Message(_ => "Changed"))));
        Assert.Contains("'task'", typedTrigger.Message);
        Assert.Contains("task-trigger", typedTrigger.Message);

        var handOff = Assert.Throws<InvalidOperationException>(() => Validate(assistant => assistant
            .AddAgent(AssistantTestData.Agent(Closer))
            .ConfigureAgent(AgentId, _ => { })
            .ConfigureAgent(Closer, run => run
                .WorksOn("project")
                .AcceptHandOffs("task", accept => accept.From(AgentId)))));
        Assert.Contains("hand-offs about 'task'", handOff.Message);

        var consultation = Assert.Throws<InvalidOperationException>(() => Validate(assistant => assistant
            .ConfigureAgent(AgentId, run => run
                .WorksOnApplication()
                .AllowConsultation("project", consult => consult.Description("Knows projects.")))));
        Assert.Contains("consultation about 'project'", consultation.Message);

        // An untyped trigger is checked when it fires; a typed one on a type the agent works on starts.
        Validate(assistant => assistant
            .ConfigureAgent(AgentId, run => run.WorksOn("project"))
            .AddTrigger<WorkItemChanged>("any-trigger", trigger => trigger
                .StartAgent(AgentId)
                .Subject(change => change.Subject)
                .Message(_ => "Changed"))
            .AddTrigger<ProjectChanged>("project-trigger", trigger => trigger
                .StartAgent(AgentId)
                .Subject("project", change => change.ProjectId.ToString("N"))
                .Message(change => change.Change)));
    }

    [Fact]
    public async Task Startup_fails_for_unknown_types_and_unregistered_policies()
    {
        var unknownType = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(assistant => assistant
            .ConfigureAgent(AgentId, run => run.WorksOn("order"))));
        Assert.Contains("works on 'order'", unknownType.Message);

        var unknownRunPolicy = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(assistant => assistant
            .ConfigureAgent(AgentId, run => run.WorksOn("task").RunPolicy("app.unknown"))));
        Assert.Contains("app.unknown", unknownRunPolicy.Message);

        var unlisted = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(assistant => assistant
            .AddAgentPolicy("app.unknown", "Nobody.")));
        Assert.Contains("AddAgentPolicy", unlisted.Message);

        var typedEvent = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(assistant => assistant
            .AddTriggerEvent<ProjectChanged>("order.changed", evt => evt
                .Description("An order changed.")
                .Subject("order", change => change.ProjectId.ToString("N"))
                .Message(change => change.Change))));
        Assert.Contains("'order'", typedEvent.Message);

        Assert.Throws<InvalidOperationException>(() => Validate(assistant => assistant
            .AddAgentPolicy("app.listed", "Listed once.")
            .AddAgentPolicy("app.listed", "Listed again otherwise.")));
    }

    private static NhAssistantAgentWorksOn Configure(Action<NhAssistantAgentRunBuilder> configure)
    {
        var options = new NhAssistantRegistrationState().GetOrAddAgentRun(AgentId);
        configure(new NhAssistantAgentRunBuilder(options));
        return options.WorksOn;
    }

    /// <summary>
    /// Registers the assistant with the configuration and checks the agent runs as startup does.
    /// </summary>
    private static void Validate(Action<NhAssistantBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddNewHeapAssistant(assistant =>
        {
            assistant
                .UsePostgreSql("Host=localhost;Database=nh_assistant_works_on", options => options.Schema = "nhai")
                .UseChatProfile("project-chat")
                .AddAgent(AssistantTestData.Agent() with { ProfileName = "project-chat" });
            configure(assistant);
        });
        services.BuildServiceProvider().GetRequiredService<NhAssistantRegistrationState>().ValidateAgentRuns();
    }

    private static async Task StartAsync(Action<NhAssistantBuilder> configure)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy(NhAssistantOptions.DefaultAccessPolicy, policy => policy.RequireAuthenticatedUser());
            options.AddPolicy(NhAssistantOptions.DefaultAdminPolicy, policy => policy.RequireAuthenticatedUser());
        });
        builder.Services.AddSingleton(new TestSubjects());
        builder.Services.AddSingleton<TestProjectToolRecorder>();
        builder.Services.AddKeyedSingleton<IChatClient>("project-chat-model", new NhAiScriptedChatClient());
        builder.Services.AddNewHeapPlatformAI(ai =>
        {
            ai.AddChatProfile("project-chat", profile => profile
                .UseKeyedClient("project-chat-model")
                .RequireCapabilities(NhAiModelCapability.FunctionCalling)
                .WithStreaming(NhAiStreamingPolicy.Allowed)
                .PermitDataClassifications(NhAiDataClassification.Internal)
                .PermitExecutionRegions("local"));
            ai.UseBudgetManager<NhAiTestBudgetManager>();
        });
        builder.Services.AddNewHeapPlatformAIAspNet(_ => { });
        builder.Services.AddNewHeapAssistant(assistant =>
        {
            assistant
                .UsePostgreSql("Host=localhost;Database=nh_assistant_works_on", options => options.Schema = "nhai")
                .UseChatProfile("project-chat")
                .AddAgent(AssistantTestData.Agent() with { ProfileName = "project-chat" })
                .AddSubjectType<TestSubjectResolver>()
                .AddSubjectType<WorksOnTaskResolver>();
            configure(assistant);
        });
        await using var app = builder.Build();
        await app.StartAsync();
        await app.StopAsync();
    }

    private async Task<AssistantTestHost> CreateHostAsync(AssistantTestProvider provider)
    {
        return await AssistantTestHost.CreateAsync(
            database,
            provider,
            new NhAiScriptedChatClient(),
            configure: services =>
            {
                services.AddSingleton(new TestSubjects());
                services.AddSingleton<RecordingOperations>();
                services.AddSingleton(provider => provider.GetRequiredService<RecordingOperations>().Service);
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, KnownAccountPrincipalFactory>());
            },
            assistantBuilder: assistant => assistant
                .AddSubjectType<TestSubjectResolver>()
                .AddSubjectType<WorksOnTaskResolver>()
                .AddAgentPolicy(ManagePolicy, "People who manage projects.")
                .AddAgentPolicy(AssistantTestHost.AccessPolicy, "Everyone who uses the assistant.")
                .AddApprovalPolicy("project-review", policy => policy
                    .Mode(NhAiApprovalMode.Deferred)
                    .Lifetime(TimeSpan.FromDays(3))
                    .Approvers(NhAiApprovers.SubjectRole(NhAiSubjectRole.Approver)))
                .AddAgent(AssistantTestData.Agent("project-chat-only"))
                .AddAgent(AssistantTestData.Agent(Closer))
                .AddTriggerEvent<WorkItemChanged>("work.changed", evt => evt
                    .Description("Some work changed.")
                    .Subject(change => change.Subject)
                    .Message(_ => "Work changed.")
                    .Dedupe(change => change.EventId.ToString("N")))
                .AddTriggerEvent<ProjectChanged>("project.changed", evt => evt
                    .Description("A project changed.")
                    .Subject("project", change => change.ProjectId.ToString("N"))
                    .Message(change => change.Change))
                .AddSubjectSet("mixed-work", "A project and a task.", MixedWorkAsync)
                .ConfigureAgent(AgentId, run => run
                    .WorksOn("project")
                    .Grant("projects.*")
                    .UseApprovalPolicy("project-review"))
                // The test host runs no startup check, so the closer accepts a type it does not work on.
                .ConfigureAgent(Closer, run => run
                    .WorksOn("project")
                    .AcceptHandOffs("task", accept => accept.From(AgentId)))
                .AddTrigger<WorkItemChanged>(WorkTrigger, trigger => trigger
                    .StartAgent(AgentId)
                    .Subject(change => change.Subject)
                    .Message(_ => "Work changed.")
                    .Dedupe(change => change.EventId.ToString("N")))
                .AddTrigger<ProjectChanged>(ProjectTrigger, trigger => trigger
                    .StartAgent(AgentId)
                    .Subject("project", change => change.ProjectId.ToString("N"))
                    .Message(change => change.Change)));
    }

    private static async IAsyncEnumerable<NhAiSubjectRef> MixedWorkAsync(
        IServiceProvider services,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        yield return new NhAiSubjectRef("project", "11111111111111111111111111111111");
        yield return new NhAiSubjectRef("task", "22222222222222222222222222222222");
    }

    private static async Task<NhAssistantHandOffReceipt> HandOffAsync(AssistantTestHost host, NhAiSubjectRef from, NhAiSubjectRef subject)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var info = (await services.GetRequiredService<NhAssistantSubjectAccessService>().DescribeAsync(from, CancellationToken.None))!;
        var agent = (await services.GetRequiredService<NhAssistantAgentCatalog>().FindAsync(AgentId, false, CancellationToken.None))!;
        var thread = (await services.GetRequiredService<NhAssistantThreadService>().GetOrCreateAsync(agent, info, CancellationToken.None)).Data!;
        var run = new AssistantRun
        {
            Id = Guid.NewGuid(),
            ConversationId = thread.ConversationId,
            TriggerId = NhAssistantTriggerIds.Manual,
            AccountableOwnerId = thread.AccountableOwnerId,
            Status = NhAssistantRunStatuses.Running,
            AgentVersion = agent.Definition.Version,
            CreatedAt = DateTimeOffset.UtcNow,
            ConcurrencyStamp = Guid.NewGuid()
        };
        await services.GetRequiredService<NhAssistantRunStore>().AddRunAsync(run, CancellationToken.None);
        var source = new NhAssistantAgentTurnContext(
            thread,
            info,
            services.GetRequiredService<NhAssistantRegistrationState>().AgentRuns[AgentId],
            run.Id,
            run.TriggerId,
            null,
            thread.AccountableOwnerId,
            IsRequestTurn: false);
        return await services.GetRequiredService<NhAssistantHandOffService>().HandOffAsync(
            new NhAssistantHandOffRequest(source, null, Closer, subject, "Please look.", null),
            CancellationToken.None);
    }

    private static NhAssistantAgentInput Input(NhAssistantAgentWorksOn? worksOn)
    {
        return new NhAssistantAgentInput(
            Follower,
            "Work follower",
            "Follows up work.",
            "Follow up the work.",
            ["projects.*"],
            [],
            null,
            NhAiAutonomyLevel.Propose,
            WorksOn: worksOn);
    }

    private static NhAssistantAgentWorksOn Works(
        IReadOnlyList<string>? types = null,
        bool? all = null,
        bool? application = null,
        bool? perDivision = null,
        string? runPolicy = null,
        string? viewPolicy = null)
    {
        return NhAssistantAgentWorksOn.FromInput(types, all, application, perDivision, runPolicy, viewPolicy)!;
    }

    private static async Task<TaskResult<NhAssistantAgent>> UpdateAsync(AssistantTestHost host, NhAssistantAgentWorksOn? worksOn, int expectedVersion)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await Administration(scope).UpdateAsync(Input(worksOn), expectedVersion, Admin, CancellationToken.None);
    }

    private static async Task<string?> FieldOfUpdateAsync(AssistantTestHost host, NhAssistantAgentWorksOn worksOn)
    {
        return Field(await UpdateAsync(host, worksOn, expectedVersion: 1));
    }

    private static async Task<NhAssistantAgent?> FindAgentAsync(AssistantTestHost host, string agentId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NhAssistantAgentCatalog>().FindAsync(agentId, includeDisabled: true, CancellationToken.None);
    }

    private static NhAssistantAgentAdministration Administration(AsyncServiceScope scope)
    {
        return scope.ServiceProvider.GetRequiredService<NhAssistantAgentAdministration>();
    }

    private static Task<TaskResult<NhAssistantDataTriggerDto>> SaveTriggerAsync(AssistantTestHost host, string triggerId, NhAssistantDataTriggerRequest request)
    {
        return ConfigureAsync(host, configuration => configuration.SaveTriggerAsync(Follower, triggerId, request, Admin));
    }

    private static async Task<T> ConfigureAsync<T>(AssistantTestHost host, Func<INhAssistantAgentConfiguration, Task<T>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<INhAssistantAgentConfiguration>());
    }

    private static async Task<IReadOnlyList<NhAssistantTriggerReceipt>> PublishAsync<TEvent>(AssistantTestHost host, TEvent evt)
        where TEvent : class
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INhAssistantAgentTriggers>().PublishAsync(evt);
    }

    /// <summary>
    /// The one field a validation failure names.
    /// </summary>
    private static string? Field(TaskResult result)
    {
        Assert.False(result.Success);
        var items = result.GetResultItems().Select(item => item.Name).ToArray();
        Assert.Equal(NhAssistantAdminErrorCodes.ValidationFailed, items.FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)));
        return items
            .Where(name => name.StartsWith("field:", StringComparison.Ordinal))
            .Select(name => name["field:".Length..])
            .Distinct()
            .Single();
    }

    private static NhAssistantRegistrationState State(AssistantTestHost host)
    {
        return host.Services.GetRequiredService<NhAssistantRegistrationState>();
    }

    private static NhAssistantRunStore Runs(AssistantTestHost host)
    {
        return host.Services.GetRequiredService<NhAssistantRunStore>();
    }

    private static StringContent Json(object value)
    {
        return new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }
}

/// <summary>
/// Some work changed: a project or a task, known only when the event is published.
/// </summary>
public sealed record WorkItemChanged(Guid EventId, NhAiSubjectRef Subject);

/// <summary>
/// Tasks of the test projects, with the roles of <see cref="TestSubjects"/> and without search.
/// </summary>
public sealed class WorksOnTaskResolver(TestSubjects subjects) : INhAiSubjectAccessResolver
{
    public string SubjectType => "task";

    public ValueTask<NhAiSubjectInfo?> DescribeAsync(NhAiSubjectRef subject, CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult<NhAiSubjectInfo?>(new NhAiSubjectInfo(subject, NhAiSubjectInfo.CreateScopeKey(null, null))
        {
            Version = subjects.Version,
            DisplayName = "Task " + subject.Id[..8]
        });
    }

    public ValueTask<NhAiSubjectAccess> ResolveAsync(NhAiSubjectRef subject, string actorId, CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(subjects.Roles.TryGetValue(actorId, out var role)
            ? new NhAiSubjectAccess(role)
            : NhAiSubjectAccess.None);
    }

    public async IAsyncEnumerable<NhAiSubjectMember> ListMembersAsync(
        NhAiSubjectRef subject,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var (actorId, role) in subjects.Roles)
        {
            await Task.Yield();
            yield return new NhAiSubjectMember(actorId, role) { DisplayName = actorId };
        }
    }
}
