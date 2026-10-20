using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Actors;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// Run now (W4 of the plan for agents working on application objects): who may run an agent by hand
/// on an object and for the whole application (K9), checked through what the agent works on with its
/// scope, and the agents of a subject with their latest run and open proposals, over HTTP on both
/// relational providers.
/// </summary>
public sealed class AssistantRunNowTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string ProjectAgent = "project-assistant";
    private const string ManagerAgent = "project-manager";
    private const string Statistics = "weekly-statistics";
    private const string DefaultStatistics = "monthly-statistics";
    private const string DivisionStatistics = "division-statistics";
    private const string LimitedStatistics = "limited-statistics";
    private const string PausedStatistics = "paused-statistics";
    private const string RunPolicy = "app.statistics.run";
    private const string Admin = "admin-1";
    private const string Runner = "runner-1";
    private const string Reader = "reader-1";
    private const string Member = "member-1";
    private const string Stranger = "stranger-1";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task The_run_policy_decides_who_runs_an_agent_for_the_whole_application_and_the_participant_role_on_an_object(AssistantTestProvider provider)
    {
        await using var app = await StartAsync(provider);
        using var admin = app.CreateClient(Admin, admin: true);
        using var runner = app.CreateClient(Runner);
        using var reader = app.CreateClient(Reader);
        using var member = app.CreateClient(Member);
        using var stranger = app.CreateClient(Stranger);

        // The run policy decides for the whole application: the person who passes it reads the whole
        // application and may start; the administrator, its approver, may not, nor may a reader.
        Assert.Equal(("reader", true), await LookupAsync(runner, Statistics, NhAiSubjectRef.SystemType, NhAiSystemSubjects.GlobalId));
        Assert.Equal(("approver", false), await LookupAsync(admin, Statistics, NhAiSubjectRef.SystemType, NhAiSystemSubjects.GlobalId));
        Assert.Equal(("reader", false), await LookupAsync(reader, Statistics, NhAiSubjectRef.SystemType, NhAiSystemSubjects.GlobalId));
        await AssertRefusedAsync(admin, Statistics, NhAiSubjectRef.SystemType, NhAiSystemSubjects.GlobalId, HttpStatusCode.Forbidden, NhAssistantErrorCodes.AgentForbidden);
        await AssertRefusedAsync(reader, Statistics, NhAiSubjectRef.SystemType, NhAiSystemSubjects.GlobalId, HttpStatusCode.Forbidden, NhAssistantErrorCodes.AgentForbidden);
        // Someone who does not read the whole application learns nothing of it.
        await AssertRefusedAsync(stranger, Statistics, NhAiSubjectRef.SystemType, NhAiSystemSubjects.GlobalId, HttpStatusCode.Forbidden, NhAssistantErrorCodes.SubjectAccessDenied);
        var started = await StartAsync(runner, Statistics, NhAiSubjectRef.SystemType, NhAiSystemSubjects.GlobalId, "Count the open projects now.");
        Assert.Equal(NhAssistantTriggerOutcomes.Started, started.GetProperty("outcome").GetString());
        var conversationId = started.GetProperty("conversationId").GetGuid();
        var thread = (await WithRunsAsync(app, runs => runs.FindThreadAsync("global", Statistics, NhAiSystemSubjects.Global, CancellationToken.None)))!;
        Assert.Equal(conversationId, thread.ConversationId);
        var run = Assert.Single((await WithRunsAsync(app, runs => runs.ListRunsAsync(conversationId, 1, 10, CancellationToken.None))).Items);
        Assert.Equal((NhAssistantTriggerIds.Manual, Runner), (run.TriggerId, run.InitiatorActorId));

        // Without a run policy the administration policy decides.
        Assert.Equal(("approver", true), await LookupAsync(admin, DefaultStatistics, NhAiSubjectRef.SystemType, NhAiSystemSubjects.GlobalId));
        Assert.Equal(("reader", false), await LookupAsync(runner, DefaultStatistics, NhAiSubjectRef.SystemType, NhAiSystemSubjects.GlobalId));
        await AssertRefusedAsync(runner, DefaultStatistics, NhAiSubjectRef.SystemType, NhAiSystemSubjects.GlobalId, HttpStatusCode.Forbidden, NhAssistantErrorCodes.AgentForbidden);

        // What the agent works on decides first, with its scope: an agent of objects never runs for
        // the whole application, an app-wide agent never in a division and a per-division agent never
        // app-wide.
        await AssertRefusedAsync(admin, ProjectAgent, NhAiSubjectRef.SystemType, NhAiSystemSubjects.GlobalId, HttpStatusCode.Conflict, NhAssistantErrorCodes.AgentDoesNotWorkOn);
        var north = NhAiSystemSubjects.ForDivision(AssistantSystemSubjectTests.North).Id;
        var south = NhAiSystemSubjects.ForDivision(AssistantSystemSubjectTests.South).Id;
        await AssertRefusedAsync(admin, Statistics, NhAiSubjectRef.SystemType, north, HttpStatusCode.Conflict, NhAssistantErrorCodes.AgentDoesNotWorkOn);
        await AssertRefusedAsync(admin, DivisionStatistics, NhAiSubjectRef.SystemType, NhAiSystemSubjects.GlobalId, HttpStatusCode.Conflict, NhAssistantErrorCodes.AgentDoesNotWorkOn);
        Assert.Equal(("approver", false), await LookupAsync(admin, Statistics, NhAiSubjectRef.SystemType, north));

        // In a division the person works in that division: the administrator runs the per-division
        // agent there; a reader of North does not read South at all.
        Assert.Equal(("approver", true), await LookupAsync(admin, DivisionStatistics, NhAiSubjectRef.SystemType, south));
        Assert.Equal(NhAssistantTriggerOutcomes.Started, (await StartAsync(admin, DivisionStatistics, NhAiSubjectRef.SystemType, south)).GetProperty("outcome").GetString());
        Assert.NotNull(await WithRunsAsync(app, runs => runs.FindThreadAsync(NhAiSubjectInfo.CreateScopeKey(null, AssistantSystemSubjectTests.South), DivisionStatistics, NhAiSystemSubjects.ForDivision(AssistantSystemSubjectTests.South), CancellationToken.None)));
        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync(LookupPath(DivisionStatistics, NhAiSubjectRef.SystemType, south))).StatusCode);
        await AssertRefusedAsync(reader, DivisionStatistics, NhAiSubjectRef.SystemType, south, HttpStatusCode.Forbidden, NhAssistantErrorCodes.SubjectAccessDenied);

        // Opening a new thread of the whole application follows the same rule.
        using (var open = await admin.PostAsync("/api/assistant/threads/open", Json(new { agentId = DefaultStatistics, subjectType = NhAiSubjectRef.SystemType, subjectId = NhAiSystemSubjects.GlobalId })))
        {
            Assert.Equal(HttpStatusCode.OK, open.StatusCode);
            Assert.True((await ReadJsonAsync(open)).GetProperty("canStart").GetBoolean());
        }
        using (var refusedOpen = await reader.PostAsync("/api/assistant/threads/open", Json(new { agentId = DivisionStatistics, subjectType = NhAiSubjectRef.SystemType, subjectId = north })))
        {
            // The refusal of the start itself: the reader reads the division but may not run the agent.
            Assert.Equal(HttpStatusCode.Forbidden, refusedOpen.StatusCode);
            Assert.Equal(NhAssistantErrorCodes.AgentForbidden, (await ReadJsonAsync(refusedOpen)).GetProperty("code").GetString());
        }

        // The subject work names the same right as the lookup.
        var work = await ReadJsonAsync(await runner.GetAsync($"/api/assistant/subjects/nh-system/global/work?agentId={Statistics}"));
        Assert.True(work.GetProperty("agents").EnumerateArray().Single(agent => agent.GetProperty("agentId").GetString() == Statistics).GetProperty("canStart").GetBoolean());
        var adminWork = await ReadJsonAsync(await admin.GetAsync($"/api/assistant/subjects/nh-system/global/work?agentId={Statistics}"));
        Assert.False(adminWork.GetProperty("agents").EnumerateArray().Single(agent => agent.GetProperty("agentId").GetString() == Statistics).GetProperty("canStart").GetBoolean());

        // On an object the participant role decides, as before; a reader of the project may not.
        var project = Guid.NewGuid().ToString("N");
        Assert.Equal(("participant", true), await LookupAsync(member, ProjectAgent, "project", project));
        Assert.Equal(("reader", false), await LookupAsync(reader, ProjectAgent, "project", project));
        await AssertRefusedAsync(reader, ProjectAgent, "project", project, HttpStatusCode.Forbidden, NhAssistantErrorCodes.SubjectAccessDenied);
        Assert.Equal(NhAssistantTriggerOutcomes.Started, (await StartAsync(member, ProjectAgent, "project", project)).GetProperty("outcome").GetString());
        // The run policy concerns the whole application only: on a project the participant may run it.
        Assert.Equal(("participant", true), await LookupAsync(member, Statistics, "project", project));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task The_agents_of_a_subject_are_those_that_work_on_it_with_their_latest_result_and_open_proposals(AssistantTestProvider provider)
    {
        await using var app = await StartAsync(provider);
        using var member = app.CreateClient(Member);
        using var reader = app.CreateClient(Reader);
        using var runner = app.CreateClient(Runner);
        // The whole application is named in the reader's language.
        runner.DefaultRequestHeaders.AcceptLanguage.ParseAdd("nl-NL");
        using var stranger = app.CreateClient(Stranger);
        var project = Guid.NewGuid().ToString("N");
        var path = $"/api/assistant/subjects/project/{project}/agents";

        // The agents that work on projects and that the person may use: the project manager needs a
        // policy the member does not pass, the statistics of the whole application only do not work
        // on projects.
        var before = await ReadJsonAsync(await member.GetAsync(path));
        Assert.Equal(("project", project, "participant"), (before.GetProperty("subjectType").GetString(), before.GetProperty("subjectId").GetString(), before.GetProperty("role").GetString()));
        Assert.Equal("Project " + project[..8], before.GetProperty("subjectDisplayName").GetString());
        var agents = before.GetProperty("agents").EnumerateArray().ToArray();
        Assert.Equal([ProjectAgent, Statistics], agents.Select(agent => agent.GetProperty("agentId").GetString()));
        Assert.All(agents, agent =>
        {
            Assert.True(agent.GetProperty("canStart").GetBoolean());
            Assert.Equal(JsonValueKind.Null, agent.GetProperty("conversationId").ValueKind);
            Assert.Equal(JsonValueKind.Null, agent.GetProperty("latestRun").ValueKind);
            Assert.Equal(0, agent.GetProperty("openProposals").GetInt32());
        });
        Assert.Equal("nh-assistant.agents.project-assistant.name", agents[0].GetProperty("displayNameKey").GetString());

        // After a run by hand: its thread, its status and time, the beginning of its result, and the
        // proposals it left open.
        var started = await StartAsync(member, ProjectAgent, "project", project, "Review the plan.");
        var runId = started.GetProperty("runId").GetGuid();
        var conversationId = started.GetProperty("conversationId").GetGuid();
        var queued = Agent(await ReadJsonAsync(await member.GetAsync(path)), ProjectAgent);
        Assert.Equal(conversationId, queued.GetProperty("conversationId").GetGuid());
        Assert.Equal((runId, NhAssistantRunStatuses.Queued, NhAssistantTriggerIds.Manual), (
            queued.GetProperty("latestRun").GetProperty("runId").GetGuid(),
            queued.GetProperty("latestRun").GetProperty("status").GetString(),
            queued.GetProperty("latestRun").GetProperty("triggerId").GetString()));
        Assert.Equal(JsonValueKind.Null, queued.GetProperty("latestRun").GetProperty("result").ValueKind);

        var summary = "2026-10-20 07:00 UTC, trigger manual, completed. " + new string('x', 400);
        Assert.True(await WithRunsAsync(app, runs => runs.TrySetRunStatusAsync(runId, [NhAssistantRunStatuses.Queued], NhAssistantRunStatuses.Completed, CancellationToken.None)));
        await WithRunsAsync(app, async runs =>
        {
            await runs.SetRunSummaryAsync(runId, summary, CancellationToken.None);
            return true;
        });
        await AddPendingProposalAsync(app, conversationId, runId);
        await AddPendingProposalAsync(app, conversationId, runId);
        await AddPendingProposalAsync(app, conversationId, runId, expired: true);

        var completed = Agent(await ReadJsonAsync(await member.GetAsync(path)), ProjectAgent);
        var latest = completed.GetProperty("latestRun");
        Assert.Equal(NhAssistantRunStatuses.Completed, latest.GetProperty("status").GetString());
        Assert.Equal(summary[..300], latest.GetProperty("result").GetString());
        Assert.True(latest.GetProperty("resultTruncated").GetBoolean());
        Assert.False(latest.GetProperty("shadow").GetBoolean());
        Assert.True(latest.GetProperty("at").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddMinutes(-5));
        Assert.Equal(2, completed.GetProperty("openProposals").GetInt32());

        // A newer run is the latest one.
        var again = await StartAsync(member, ProjectAgent, "project", project);
        Assert.Equal(again.GetProperty("runId").GetGuid(), Agent(await ReadJsonAsync(await member.GetAsync(path)), ProjectAgent).GetProperty("latestRun").GetProperty("runId").GetGuid());

        // A reader sees the same work and may run nothing; someone without a role sees nothing.
        var asReader = await ReadJsonAsync(await reader.GetAsync(path));
        Assert.Equal("reader", asReader.GetProperty("role").GetString());
        Assert.All(asReader.GetProperty("agents").EnumerateArray(), agent => Assert.False(agent.GetProperty("canStart").GetBoolean()));
        Assert.Equal(2, Agent(asReader, ProjectAgent).GetProperty("openProposals").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync(path)).StatusCode);

        // The whole application lists the agents that work on it; the run policy decides who runs them.
        var application = await ReadJsonAsync(await runner.GetAsync("/api/assistant/subjects/nh-system/global/agents"));
        Assert.Equal(("De hele applicatie", "reader"), (application.GetProperty("subjectDisplayName").GetString(), application.GetProperty("role").GetString()));
        var system = application.GetProperty("agents").EnumerateArray().ToDictionary(agent => agent.GetProperty("agentId").GetString()!);
        Assert.Equal([DefaultStatistics, Statistics], system.Keys);
        Assert.Equal((false, true), (system[DefaultStatistics].GetProperty("canStart").GetBoolean(), system[Statistics].GetProperty("canStart").GetBoolean()));
        using (var admin = app.CreateClient(Admin, admin: true))
        {
            // In a division, the agents that work per division.
            var division = await ReadJsonAsync(await admin.GetAsync($"/api/assistant/subjects/nh-system/{NhAiSystemSubjects.ForDivision(AssistantSystemSubjectTests.North).Id}/agents"));
            Assert.Equal(("North", "approver"), (division.GetProperty("subjectDisplayName").GetString(), division.GetProperty("role").GetString()));
            var perDivision = Assert.Single(division.GetProperty("agents").EnumerateArray());
            Assert.Equal((DivisionStatistics, true), (perDivision.GetProperty("agentId").GetString(), perDivision.GetProperty("canStart").GetBoolean()));
        }

        // Unknown and invalid subjects.
        Assert.Equal(NhAssistantErrorCodes.SubjectTypeUnknown, (await ReadJsonAsync(await member.GetAsync($"/api/assistant/subjects/order/{project}/agents"))).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await member.GetAsync($"/api/assistant/subjects/Not_A_Type/{project}/agents")).StatusCode);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task People_find_the_divisions_of_the_whole_application_by_name_when_the_application_lists_them(AssistantTestProvider provider)
    {
        await using var app = await StartAsync(provider);
        using var admin = app.CreateClient(Admin, admin: true);
        using var reader = app.CreateClient(Reader);

        var catalog = await ReadJsonAsync(await admin.GetAsync("/api/assistant/admin/run-catalog"));
        var system = catalog.GetProperty("subjectTypes").EnumerateArray().Single(type => type.GetProperty("id").GetString() == NhAiSubjectRef.SystemType);
        Assert.True(system.GetProperty("searchable").GetBoolean());

        var found = await ReadJsonAsync(await admin.GetAsync("/api/assistant/subjects/nh-system/search?query=or"));
        Assert.Equal(
            [("North", NhAiSystemSubjects.ForDivision(AssistantSystemSubjectTests.North).Id)],
            found.GetProperty("items").EnumerateArray().Select(item => (item.GetProperty("displayName").GetString(), item.GetProperty("id").GetString())));
        var all = await ReadJsonAsync(await admin.GetAsync("/api/assistant/subjects/nh-system/search?query=th"));
        Assert.Equal(["North", "South"], all.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("displayName").GetString()));
        // Only the divisions the person may read.
        var readable = await ReadJsonAsync(await reader.GetAsync("/api/assistant/subjects/nh-system/search?query=th"));
        Assert.Equal(["North"], readable.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("displayName").GetString()));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_start_by_hand_waits_for_a_paused_agent_and_a_refused_start_answers_its_code_with_its_status(AssistantTestProvider provider)
    {
        await using var app = await StartAsync(provider, limitedAgents: true);
        using var admin = app.CreateClient(Admin, admin: true);
        var global = NhAiSystemSubjects.GlobalId;

        // A paused agent lets the start wait in its thread: 202 with the reason, no run yet.
        await using (var scope = app.Services.CreateAsyncScope())
        {
            Assert.True((await scope.ServiceProvider.GetRequiredService<INhAssistantAgentControl>().PauseAsync(PausedStatistics, Admin)).Success);
        }
        var waiting = await StartAsync(admin, PausedStatistics, NhAiSubjectRef.SystemType, global);
        Assert.Equal(NhAssistantTriggerOutcomes.Waiting, waiting.GetProperty("outcome").GetString());
        Assert.Equal(NhAssistantErrorCodes.AgentPaused, waiting.GetProperty("reasonCode").GetString());
        Assert.Equal(JsonValueKind.Null, waiting.GetProperty("runId").ValueKind);

        // The agent's daily limit for the subject refuses the next start with its public code.
        Assert.Equal(NhAssistantTriggerOutcomes.Started, (await StartAsync(admin, LimitedStatistics, NhAiSubjectRef.SystemType, global)).GetProperty("outcome").GetString());
        await AssertRefusedAsync(admin, LimitedStatistics, NhAiSubjectRef.SystemType, global, HttpStatusCode.Conflict, NhAssistantTriggerReasons.RateLimited);

        // A malformed subject is invalid, an unknown agent is not found.
        await AssertRefusedAsync(admin, LimitedStatistics, NhAiSubjectRef.SystemType, " padded ", HttpStatusCode.BadRequest, NhAssistantErrorCodes.SubjectInvalid);
        await AssertRefusedAsync(admin, "no-such-agent", NhAiSubjectRef.SystemType, global, HttpStatusCode.NotFound, NhAssistantErrorCodes.AgentNotFound);
    }

    private Task<AssistantWebApplication> StartAsync(AssistantTestProvider provider, bool limitedAgents = false)
    {
        return AssistantWebApplication.StartAsync(
            database,
            new NhAiScriptedChatClient(),
            configure: services =>
            {
                var subjects = new TestSubjects();
                subjects.Roles[Reader] = NhAiSubjectRole.Reader;
                services.AddSingleton(subjects);
                services.AddSingleton<RecordingOperations>();
                services.AddSingleton(provider => provider.GetRequiredService<RecordingOperations>().Service);
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, RunNowPrincipals>());
                services.AddNewHeapDivisionAccess<TestDivisionAccess>();
                services.AddNewHeapDivisionDirectory<TestDivisionDirectory>();
                services.AddAuthorization(options => options.AddPolicy(RunPolicy, policy => policy.RequireClaim("permission", RunPolicy)));
                // Runs are recorded, not executed: the startup check would require their background operations.
                services.Remove(services.Single(descriptor => descriptor.ImplementationType == typeof(NhAssistantStartupValidator)));
            },
            assistantBuilder: assistant =>
            {
                assistant
                    .AddSubjectType<TestSubjectResolver>(type => type.Named(new() { ["en"] = "Project" }, fallbackLanguage: "en"))
                    .AddAgent(AssistantTestData.Agent(Statistics))
                    .AddAgent(AssistantTestData.Agent(DefaultStatistics))
                    .AddAgent(AssistantTestData.Agent(DivisionStatistics))
                    .ConfigureAgent(ProjectAgent, run => run.WorksOn("project"))
                    .ConfigureAgent(ManagerAgent, run => run.WorksOn("project"))
                    .ConfigureAgent(Statistics, run => run.WorksOn("project").WorksOnApplication().RunPolicy(RunPolicy))
                    .ConfigureAgent(DefaultStatistics, run => run.WorksOnApplication())
                    .ConfigureAgent(DivisionStatistics, run => run.WorksOnApplication().PerDivision());
                if (limitedAgents)
                {
                    assistant
                        .AddAgent(AssistantTestData.Agent(LimitedStatistics))
                        .AddAgent(AssistantTestData.Agent(PausedStatistics))
                        .ConfigureAgent(LimitedStatistics, run => run
                            .WorksOnApplication()
                            .Budget(NhAssistantAgentBudget.PerDay(toolCalls: 100, tokens: 1_000_000), maxRunsPerSubjectPerDay: 1))
                        .ConfigureAgent(PausedStatistics, run => run.WorksOnApplication());
                }
            },
            provider: provider);
    }

    private static string LookupPath(string agentId, string subjectType, string subjectId)
    {
        return $"/api/assistant/threads?agentId={agentId}&subjectType={subjectType}&subjectId={subjectId}";
    }

    private static async Task<(string Role, bool CanStart)> LookupAsync(HttpClient client, string agentId, string subjectType, string subjectId)
    {
        using var response = await client.GetAsync(LookupPath(agentId, subjectType, subjectId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var lookup = await ReadJsonAsync(response);
        return (lookup.GetProperty("role").GetString()!, lookup.GetProperty("canStart").GetBoolean());
    }

    private static async Task<JsonElement> StartAsync(HttpClient client, string agentId, string subjectType, string subjectId, string? message = null)
    {
        using var response = await client.PostAsync("/api/assistant/threads/start", Json(new { agentId, subjectType, subjectId, message }));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return await ReadJsonAsync(response);
    }

    private static async Task AssertRefusedAsync(HttpClient client, string agentId, string subjectType, string subjectId, HttpStatusCode status, string code)
    {
        using var response = await client.PostAsync("/api/assistant/threads/start", Json(new { agentId, subjectType, subjectId }));
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, (await ReadJsonAsync(response)).GetProperty("code").GetString());
    }

    private static JsonElement Agent(JsonElement listing, string agentId)
    {
        return listing.GetProperty("agents").EnumerateArray().Single(agent => agent.GetProperty("agentId").GetString() == agentId);
    }

    private static async Task AddPendingProposalAsync(AssistantWebApplication app, Guid conversationId, Guid runId, bool expired = false)
    {
        var now = DateTimeOffset.UtcNow;
        await using var scope = app.Services.CreateAsyncScope();
        await using var context = scope.ServiceProvider.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        context.Approvals.Add(new AssistantApproval
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            TurnId = Guid.NewGuid(),
            ToolInvocationId = Guid.NewGuid(),
            ProposalId = Guid.NewGuid(),
            ProposalHash = "sha256:" + Guid.NewGuid().ToString("N"),
            ProposalJson = "{}",
            ToolId = "projects.complete",
            Summary = "Complete the project.",
            ArgumentsPreview = "{}",
            Status = NhAssistantApprovalStatuses.Pending,
            CreatedAt = now.AddMinutes(-2),
            ExpiresAt = expired ? now.AddMinutes(-1) : now.AddDays(1),
            RunId = runId,
            Mode = NhAssistantApprovalModes.Deferred,
            ConcurrencyStamp = Guid.NewGuid()
        });
        await context.SaveChangesAsync();
    }

    private static async Task<T> WithRunsAsync<T>(AssistantWebApplication app, Func<NhAssistantRunStore, Task<T>> action)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<NhAssistantRunStore>());
    }

    private static HttpContent Json(object body)
    {
        return ServerSentEventReader.Json(body);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    /// <summary>
    /// The people as headless runs see them: the administrator passes the administration policy, the
    /// runner the statistics run policy, the reader and the member only use the assistant.
    /// </summary>
    private sealed class RunNowPrincipals : INhAiPrincipalFactory
    {
        public ValueTask<ClaimsPrincipal?> CreateAsync(string actorId, NhAiSubjectInfo subject, CancellationToken cancellationToken = default)
        {
            var permissions = actorId switch
            {
                Admin => new[] { "assistant", "assistant-admin" },
                Runner => new[] { "assistant", RunPolicy },
                Reader or Member => new[] { "assistant" },
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
