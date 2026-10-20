using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Actors;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// Groups and boards (W7 of the plan for agents working on application objects, K15 and K16): groups
/// in code with names per language, administrators' memberships that win over code, boards that show
/// people the agents they may see with their latest result for the whole application and what is new
/// since their last visit per scope, and the thread of the whole application kept to the agent's view
/// policy; on both relational providers.
/// </summary>
[Collection(AssistantDatabaseCollection.Name)]
public sealed class AssistantAgentBoardTests(AssistantDatabaseFixture database)
{
    private const string Group = "project-management";
    private const string Reporting = "reporting";
    private const string Restricted = "follow-up-only";
    private const string FollowUp = "project-follow-up";
    private const string Statistics = "weekly-statistics";
    private const string Regional = "regional-review";
    private const string ProjectAgent = "project-assistant";
    private const string ViewPolicy = "app.results.view";
    private const string Admin = "admin-1";
    private const string Manager = "manager-1";
    private const string Reader = "reader-1";
    private const string Viewer = "viewer-1";

    [Fact]
    public void Groups_are_named_in_code_and_agents_join_registered_groups_only()
    {
        Assert.Throws<ArgumentException>(() => Validate(assistant => assistant.AddAgentGroup("Project_Management", Named)));
        Assert.Throws<ArgumentException>(() => Validate(assistant => assistant.AddAgentGroup(new string('a', 65), Named)));
        var nameless = Assert.Throws<InvalidOperationException>(() => Validate(assistant => assistant.AddAgentGroup(Group, _ => { })));
        Assert.Contains("no name", nameless.Message);
        Assert.Throws<ArgumentException>(() => Validate(assistant => assistant.AddAgentGroup(Group, group => group
            .Named(new() { ["nl"] = "Projectbeheer" }, fallbackLanguage: "en"))));
        Assert.Throws<ArgumentException>(() => Validate(assistant => assistant.AddAgentGroup(Group, group => group
            .Named(new() { ["en"] = new string('n', 101) }, fallbackLanguage: "en"))));
        var description = Assert.Throws<InvalidOperationException>(() => Validate(assistant => assistant.AddAgentGroup(Group, group => group
            .Named(new() { ["en"] = "Project management" }, fallbackLanguage: "en")
            .Describe(new() { ["nl"] = "Agents die projecten op gang houden." }))));
        Assert.Contains("fallback language", description.Message);
        // The same group from two compositions is kept; another group with the same id is refused.
        Validate(assistant => assistant
            .AddAgentGroup(Group, Named)
            .AddAgentGroup(Group, Named));
        var other = Assert.Throws<InvalidOperationException>(() => Validate(assistant => assistant
            .AddAgentGroup(Group, Named)
            .AddAgentGroup(Group, group => group.Named(new() { ["en"] = "Projects" }, fallbackLanguage: "en"))));
        Assert.Contains("already registered", other.Message);
        Assert.Throws<ArgumentException>(() => Validate(assistant => assistant
            .ConfigureAgent(ProjectAgent, run => run.InGroup("Not A Group"))));

        // Startup fails for an agent that joins a group nobody registered.
        var unknown = Assert.Throws<InvalidOperationException>(() => Validate(assistant => assistant
            .ConfigureAgent(ProjectAgent, run => run.InGroup(Group))));
        Assert.Contains($"joins group '{Group}', which is not registered", unknown.Message);
        Validate(assistant => assistant
            .AddAgentGroup(Group, Named)
            .ConfigureAgent(ProjectAgent, run => run.InGroup(Group).InGroup(Group)));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Administrators_put_agents_in_groups_and_their_choice_wins_over_code(AssistantTestProvider provider)
    {
        await using var app = await StartAsync(provider);
        using var admin = app.CreateClient(Admin, admin: true);

        // The run catalog names the groups in the request's language.
        using (var dutch = new HttpRequestMessage(HttpMethod.Get, "/api/assistant/admin/run-catalog"))
        {
            dutch.Headers.AcceptLanguage.ParseAdd("nl-NL");
            var groups = (await ReadJsonAsync(await admin.SendAsync(dutch))).GetProperty("agentGroups").EnumerateArray().ToArray();
            Assert.Equal([Restricted, Group, Reporting], groups.Select(group => group.GetProperty("id").GetString()));
            Assert.Equal(("Projectbeheer", "Agents die de projecten op gang houden."), (
                groups[1].GetProperty("name").GetString(),
                groups[1].GetProperty("description").GetString()));
            Assert.Equal(JsonValueKind.Null, groups[2].GetProperty("description").ValueKind);
        }

        // The agents carry the groups they are in and those of their code.
        var agents = await AdminAgentsAsync(admin);
        Assert.Equal([Restricted, Group], Strings(agents[FollowUp], "groups"));
        Assert.Equal([Restricted, Group], Strings(agents[FollowUp], "codeGroups"));
        Assert.Empty(Strings(agents[ProjectAgent], "groups"));
        Assert.Empty(Strings(agents[ProjectAgent], "codeGroups"));

        // Taking an agent out of a group of its code, and putting another agent in, wins over code.
        var removed = await ReadJsonAsync(await admin.PutAsJsonAsync($"/api/assistant/admin/agents/{FollowUp}/groups", new { groups = new[] { Restricted } }));
        Assert.Equal([Restricted], Strings(removed, "groups"));
        Assert.Equal([Restricted, Group], Strings(removed, "codeGroups"));
        var added = await ReadJsonAsync(await admin.PutAsJsonAsync($"/api/assistant/admin/agents/{ProjectAgent}/groups", new { groups = new[] { Reporting, Group, Group } }));
        Assert.Equal([Group, Reporting], Strings(added, "groups"));
        Assert.Equal([(Group, false)], await ChoicesAsync(app, FollowUp));
        Assert.Equal([(Group, true), (Reporting, true)], await ChoicesAsync(app, ProjectAgent));
        agents = await AdminAgentsAsync(admin);
        Assert.Equal([Restricted], Strings(agents[FollowUp], "groups"));
        Assert.Equal([Group, Reporting], Strings(agents[ProjectAgent], "groups"));

        // Boards follow the memberships at once.
        using var manager = app.CreateClient(Manager, admin: true);
        var board = await ReadJsonAsync(await manager.GetAsync($"/api/assistant/boards/{Group}"));
        Assert.Equal([ProjectAgent, Regional, Statistics], AgentIds(board));

        // A choice that equals code lets the code decide again: nothing is stored for it.
        var restored = await ReadJsonAsync(await admin.PutAsJsonAsync($"/api/assistant/admin/agents/{FollowUp}/groups", new { groups = new[] { Group, Restricted } }));
        Assert.Equal([Restricted, Group], Strings(restored, "groups"));
        Assert.Empty(await ChoicesAsync(app, FollowUp));

        // Unknown groups, a missing list, an unknown agent and people who do not administer are refused.
        await AssertFieldErrorAsync(await admin.PutAsJsonAsync($"/api/assistant/admin/agents/{FollowUp}/groups", new { groups = new[] { "unknown-group" } }), "not-found");
        await AssertFieldErrorAsync(await admin.PutAsJsonAsync($"/api/assistant/admin/agents/{FollowUp}/groups", new { other = 1 }), "required");
        using (var unknown = await admin.PutAsJsonAsync("/api/assistant/admin/agents/unknown-agent/groups", new { groups = new[] { Group } }))
        {
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        }
        using (var refused = await app.CreateClient(Reader).PutAsJsonAsync($"/api/assistant/admin/agents/{FollowUp}/groups", new { groups = Array.Empty<string>() }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        // A save that cannot be stored, such as one for an agent deleted meanwhile, fails as a
        // conflict result instead of an exception.
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var saved = await scope.ServiceProvider.GetRequiredService<NhAssistantBoardStore>().SetMembershipsAsync(
                "deleted-agent",
                new Dictionary<string, bool?> { [Group] = true },
                Admin,
                DateTimeOffset.UtcNow,
                CancellationToken.None);
            Assert.False(saved.Success);
            Assert.Contains(saved.GetResultItems(), item => item.Name == NhAssistantAdminErrorCodes.VersionConflict);
        }
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Boards_show_people_the_agents_they_see_and_what_is_new_since_their_last_visit_per_scope(AssistantTestProvider provider)
    {
        await using var app = await StartAsync(provider);
        var now = DateTimeOffset.UtcNow;
        var statistics = await SeedRunAsync(app, Statistics, NhAiSystemSubjects.Global, now.AddHours(-2), "**12** projects are open.");
        await SeedRunAsync(app, FollowUp, NhAiSystemSubjects.Global, now.AddHours(-1), "Proposed completing PRJ-1.");
        var north = await SeedRunAsync(app, Regional, NhAiSystemSubjects.ForDivision(AssistantSystemSubjectTests.North), now.AddMinutes(-30), "North is on track.");

        // People see the groups with at least one agent they see: the follow-up only by its view policy.
        using var reader = app.CreateClient(Reader);
        reader.DefaultRequestHeaders.AcceptLanguage.ParseAdd("nl-NL");
        var list = (await ReadJsonAsync(await reader.GetAsync("/api/assistant/boards"))).GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal([(Group, "Projectbeheer", 2), (Reporting, "Reporting", 1)], list.Select(item => (
            item.GetProperty("id").GetString(),
            item.GetProperty("name").GetString(),
            item.GetProperty("agentCount").GetInt32())));
        using var viewer = app.CreateClient(Viewer);
        Assert.Equal([Restricted, Group, Reporting], (await ReadJsonAsync(await viewer.GetAsync("/api/assistant/boards"))).GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString()));
        await AssertBoardNotFoundAsync(reader, $"/api/assistant/boards/{Restricted}");
        await AssertBoardNotFoundAsync(reader, "/api/assistant/boards/unknown-group");

        // A board of the whole application: per agent what it works on, its latest result there, and
        // everything is new before the first visit. An agent that works per division has no work for
        // the whole application here.
        var board = await ReadJsonAsync(await reader.GetAsync($"/api/assistant/boards/{Group}"));
        Assert.Equal(("global", JsonValueKind.Null, JsonValueKind.Null), (
            board.GetProperty("scopeKey").GetString(),
            board.GetProperty("divisionId").ValueKind,
            board.GetProperty("lastVisitedAt").ValueKind));
        Assert.Equal([Regional, Statistics], AgentIds(board));
        var tile = Agent(board, Statistics);
        Assert.Equal(("nh-system", "global", "De hele applicatie"), (
            tile.GetProperty("subjectType").GetString(),
            tile.GetProperty("subjectId").GetString(),
            tile.GetProperty("subjectDisplayName").GetString()));
        Assert.Equal((statistics, "**12** projects are open.", true, false), (
            tile.GetProperty("latestRun").GetProperty("runId").GetGuid(),
            tile.GetProperty("latestRun").GetProperty("result").GetString(),
            tile.GetProperty("isNew").GetBoolean(),
            tile.GetProperty("canStart").GetBoolean()));
        Assert.Equal(JsonValueKind.String, tile.GetProperty("conversationId").ValueKind);
        var worksOn = tile.GetProperty("worksOn");
        Assert.Equal((false, true, false), (worksOn.GetProperty("allObjects").GetBoolean(), worksOn.GetProperty("application").GetBoolean(), worksOn.GetProperty("perDivision").GetBoolean()));
        Assert.Equal(("project", "Project", "Projecten"), (
            worksOn.GetProperty("subjectTypes")[0].GetProperty("id").GetString(),
            worksOn.GetProperty("subjectTypes")[0].GetProperty("name").GetString(),
            worksOn.GetProperty("subjectTypes")[0].GetProperty("pluralName").GetString()));
        var regional = Agent(board, Regional);
        Assert.Equal((JsonValueKind.Null, JsonValueKind.Null, false, false), (
            regional.GetProperty("subjectType").ValueKind,
            regional.GetProperty("latestRun").ValueKind,
            regional.GetProperty("isNew").GetBoolean(),
            regional.GetProperty("canStart").GetBoolean()));
        Assert.True(regional.GetProperty("worksOn").GetProperty("perDivision").GetBoolean());

        // Who may run the agent for the whole application may start it from the board.
        using var admin = app.CreateClient(Admin, admin: true);
        Assert.True(Agent(await ReadJsonAsync(await admin.GetAsync($"/api/assistant/boards/{Group}")), Statistics).GetProperty("canStart").GetBoolean());

        // A visit marks what came before as seen; what ends or is proposed later is new again.
        var visit = await ReadJsonAsync(await reader.PostAsync($"/api/assistant/boards/{Group}/visit", null));
        Assert.Equal((Group, "global"), (visit.GetProperty("groupId").GetString(), visit.GetProperty("scopeKey").GetString()));
        board = await ReadJsonAsync(await reader.GetAsync($"/api/assistant/boards/{Group}"));
        Assert.Equal(visit.GetProperty("visitedAt").GetDateTimeOffset(), board.GetProperty("lastVisitedAt").GetDateTimeOffset());
        Assert.False(Agent(board, Statistics).GetProperty("isNew").GetBoolean());
        // A run that ends after the visit (it started 30 seconds before it ended).
        var later = await SeedRunAsync(app, Statistics, NhAiSystemSubjects.Global, DateTimeOffset.UtcNow.AddSeconds(-30), "**13** projects are open.");
        tile = Agent(await ReadJsonAsync(await reader.GetAsync($"/api/assistant/boards/{Group}")), Statistics);
        Assert.Equal((later, true), (tile.GetProperty("latestRun").GetProperty("runId").GetGuid(), tile.GetProperty("isNew").GetBoolean()));
        await ReadJsonAsync(await reader.PostAsync($"/api/assistant/boards/{Group}/visit", null));
        Assert.False(Agent(await ReadJsonAsync(await reader.GetAsync($"/api/assistant/boards/{Group}")), Statistics).GetProperty("isNew").GetBoolean());
        var thread = await ThreadAtAsync(app, Statistics, NhAiSystemSubjects.Global);
        await AddProposalAsync(app, thread.ConversationId, later, DateTimeOffset.UtcNow.AddSeconds(2));
        tile = Agent(await ReadJsonAsync(await reader.GetAsync($"/api/assistant/boards/{Group}")), Statistics);
        Assert.Equal((1, true), (tile.GetProperty("openProposals").GetInt32(), tile.GetProperty("isNew").GetBoolean()));
        // Proposals its runs placed at objects count as well.
        var atProject = await ThreadAtAsync(app, Statistics, new NhAiSubjectRef("project", Guid.NewGuid().ToString()));
        await AddProposalAsync(app, atProject.ConversationId, later, DateTimeOffset.UtcNow, placedFrom: thread.ConversationId);
        Assert.Equal(2, Agent(await ReadJsonAsync(await reader.GetAsync($"/api/assistant/boards/{Group}")), Statistics).GetProperty("openProposals").GetInt32());
        // Visits are per person: another person's board is still all new.
        Assert.True(Agent(await ReadJsonAsync(await admin.GetAsync($"/api/assistant/boards/{Group}")), Statistics).GetProperty("isNew").GetBoolean());

        // The board of a division shows the per-division agent's work there, and its visits are its own:
        // the visits of the whole application's board do not count.
        var divisionBoard = await ReadJsonAsync(await reader.GetAsync($"/api/assistant/boards/{Group}?divisionId={AssistantSystemSubjectTests.North}"));
        Assert.Equal(($"division:{AssistantSystemSubjectTests.North:N}", AssistantSystemSubjectTests.North.ToString("D"), JsonValueKind.Null), (
            divisionBoard.GetProperty("scopeKey").GetString(),
            divisionBoard.GetProperty("divisionId").GetString(),
            divisionBoard.GetProperty("lastVisitedAt").ValueKind));
        regional = Agent(divisionBoard, Regional);
        Assert.Equal(($"division-{AssistantSystemSubjectTests.North:N}", "North", north, true), (
            regional.GetProperty("subjectId").GetString(),
            regional.GetProperty("subjectDisplayName").GetString(),
            regional.GetProperty("latestRun").GetProperty("runId").GetGuid(),
            regional.GetProperty("isNew").GetBoolean()));
        Assert.True(Agent(divisionBoard, Statistics).GetProperty("isNew").GetBoolean());
        await ReadJsonAsync(await reader.PostAsync($"/api/assistant/boards/{Group}/visit?divisionId={AssistantSystemSubjectTests.North}", null));
        Assert.False(Agent(await ReadJsonAsync(await reader.GetAsync($"/api/assistant/boards/{Group}?divisionId={AssistantSystemSubjectTests.North}")), Regional).GetProperty("isNew").GetBoolean());
        // One visit per person, group and scope: the whole application's and North's.
        Assert.Equal(2, await VisitCountAsync(app, Reader));

        // A division the person may not work in, an unknown one and a malformed id are refused.
        await AssertBoardNotFoundAsync(reader, $"/api/assistant/boards/{Group}?divisionId={AssistantSystemSubjectTests.South}");
        await AssertBoardNotFoundAsync(reader, $"/api/assistant/boards/{Group}?divisionId={Guid.NewGuid()}");
        using (var malformed = await reader.GetAsync($"/api/assistant/boards/{Group}?divisionId=north"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
            Assert.Equal(NhAssistantErrorCodes.BoardScopeInvalid, (await ReadJsonAsync(malformed, HttpStatusCode.BadRequest)).GetProperty("code").GetString());
        }
        using (var refused = await reader.PostAsync($"/api/assistant/boards/{Restricted}/visit", null))
        {
            Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        }
        Assert.Equal(2, await VisitCountAsync(app, Reader));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task The_thread_of_the_whole_application_is_read_by_the_agents_view_policy_and_decided_by_its_approvers(AssistantTestProvider provider)
    {
        await using var app = await StartAsync(provider);
        var runId = await SeedRunAsync(app, FollowUp, NhAiSystemSubjects.Global, DateTimeOffset.UtcNow.AddMinutes(-5), "Proposed completing PRJ-1.");
        var thread = await ThreadAtAsync(app, FollowUp, NhAiSystemSubjects.Global);
        var approvalId = await AddProposalAsync(app, thread.ConversationId, runId, DateTimeOffset.UtcNow.AddMinutes(-4));
        var artifactId = await AddArtifactAsync(app, thread.ConversationId, runId);
        await SeedRunAsync(app, Statistics, NhAiSystemSubjects.Global, DateTimeOffset.UtcNow.AddMinutes(-6), "**12** projects are open.");
        var lookup = $"/api/assistant/threads?agentId={FollowUp}&subjectType=nh-system&subjectId=global";

        // A reader of the whole application who does not pass the agent's view policy reads none of its
        // thread: not the lookup, the conversation, its proposals, files or runs, nor the subject's work.
        using var reader = app.CreateClient(Reader);
        foreach (var path in new[]
        {
            lookup,
            $"/api/assistant/conversations/{thread.ConversationId}",
            $"/api/assistant/threads/{thread.ConversationId}/proposals",
            $"/api/assistant/threads/{thread.ConversationId}/runs",
            $"/api/assistant/threads/{thread.ConversationId}/artifacts",
            $"/api/assistant/threads/{thread.ConversationId}/artifacts/{artifactId}/content",
            $"/api/assistant/approvals/{approvalId}",
            $"/api/assistant/runs/{runId}"
        })
        {
            using var refused = await reader.GetAsync(path);
            Assert.True(refused.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden, $"{path} answered {refused.StatusCode}.");
        }
        var work = await ReadJsonAsync(await reader.GetAsync("/api/assistant/subjects/nh-system/global/work"));
        Assert.DoesNotContain(FollowUp, work.GetProperty("agents").EnumerateArray().Select(agent => agent.GetProperty("agentId").GetString()));
        Assert.Contains(Statistics, work.GetProperty("agents").EnumerateArray().Select(agent => agent.GetProperty("agentId").GetString()));
        var objectAgents = await ReadJsonAsync(await reader.GetAsync("/api/assistant/subjects/nh-system/global/agents"));
        Assert.DoesNotContain(FollowUp, objectAgents.GetProperty("agents").EnumerateArray().Select(agent => agent.GetProperty("agentId").GetString()));
        // An invitation does not open it either.
        await using (var scope = app.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<INhAssistantStore>().TryAddParticipantAsync(
                new AssistantConversationParticipant
                {
                    ConversationId = thread.ConversationId,
                    ActorId = Reader,
                    DisplayName = Reader,
                    JoinedVia = "invitation",
                    JoinedAt = DateTimeOffset.UtcNow
                },
                int.MaxValue,
                CancellationToken.None);
        }
        using (var invited = await reader.GetAsync($"/api/assistant/conversations/{thread.ConversationId}"))
        {
            Assert.NotEqual(HttpStatusCode.OK, invited.StatusCode);
        }

        // Whoever passes the view policy and reads the whole application reads all of it.
        using var viewer = app.CreateClient(Viewer);
        var asViewer = await ReadJsonAsync(await viewer.GetAsync(lookup));
        Assert.Equal(("reader", false), (asViewer.GetProperty("role").GetString(), asViewer.GetProperty("canStart").GetBoolean()));
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"/api/assistant/conversations/{thread.ConversationId}")).StatusCode);
        Assert.Equal(approvalId, Assert.Single((await ReadJsonAsync(await viewer.GetAsync($"/api/assistant/threads/{thread.ConversationId}/proposals"))).EnumerateArray()).GetProperty("approvalId").GetGuid());
        Assert.Equal(approvalId, (await ReadJsonAsync(await viewer.GetAsync($"/api/assistant/approvals/{approvalId}"))).GetProperty("proposal").GetProperty("approvalId").GetGuid());
        Assert.True((await ReadJsonAsync(await viewer.GetAsync($"/api/assistant/runs/{runId}"))).GetProperty("canOpenConversation").GetBoolean());

        // An approver of the whole application without the view policy neither reads the thread nor
        // starts the agent there, whose thread it could not open, but still decides its proposals.
        using var admin = app.CreateClient(Admin, admin: true);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync(lookup)).StatusCode);
        using (var start = await admin.PostAsJsonAsync("/api/assistant/threads/start", new { agentId = FollowUp, subjectType = "nh-system", subjectId = "global" }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, start.StatusCode);
            Assert.Equal(NhAssistantErrorCodes.SubjectAccessDenied, (await ReadJsonAsync(start, HttpStatusCode.Forbidden)).GetProperty("code").GetString());
        }
        var inbox = await ReadJsonAsync(await admin.GetAsync("/api/assistant/approvals/inbox"));
        Assert.Equal(approvalId, Assert.Single(inbox.GetProperty("items").EnumerateArray()).GetProperty("approvalId").GetGuid());
        using (var decided = await admin.PostAsJsonAsync($"/api/assistant/approvals/{approvalId}/decisions", new { decision = "reject", reason = "Not now." }))
        {
            Assert.Equal(NhAssistantApprovalStatuses.Rejected, (await ReadJsonAsync(decided)).GetProperty("status").GetString());
        }

        // Someone who passes the run policy and the view policy starts it.
        using var manager = app.CreateClient(Manager, admin: true);
        Assert.True((await ReadJsonAsync(await manager.GetAsync(lookup))).GetProperty("canStart").GetBoolean());
    }

    private static void Named(NhAssistantAgentGroupBuilder group)
    {
        group.Named(new() { ["en"] = "Project management", ["nl"] = "Projectbeheer" }, fallbackLanguage: "en");
    }

    private static void Validate(Action<NhAssistantBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddNewHeapAssistant(assistant =>
        {
            assistant
                .UsePostgreSql("Host=localhost;Database=nh_assistant_boards", options => options.Schema = "nhai")
                .UseChatProfile("project-chat")
                .AddAgent(AssistantTestData.Agent() with { ProfileName = "project-chat" });
            configure(assistant);
        });
        services.BuildServiceProvider().GetRequiredService<NhAssistantRegistrationState>().ValidateAgentRuns();
    }

    private Task<AssistantWebApplication> StartAsync(AssistantTestProvider provider)
    {
        return AssistantWebApplication.StartAsync(
            database,
            new NhAiScriptedChatClient(),
            configure: services =>
            {
                services.AddSingleton<BoardProjects>();
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, BoardPrincipals>());
                services.AddNewHeapDivisionDirectory<TestDivisionDirectory>();
                services.AddNewHeapDivisionAccess<TestDivisionAccess>();
                services.AddAuthorization(options => options.AddPolicy(ViewPolicy, policy => policy.RequireClaim("permission", ViewPolicy)));
                // Runs are seeded here, not executed by background operations.
                services.Remove(services.Single(descriptor => descriptor.ImplementationType == typeof(NhAssistantStartupValidator)));
            },
            assistantBuilder: assistant => assistant
                .AddSubjectType<BoardProjectResolver>(type => type
                    .Named(new() { ["en"] = "Project", ["nl"] = "Project" }, fallbackLanguage: "en")
                    .NamedPlural(new() { ["en"] = "Projects", ["nl"] = "Projecten" }))
                .AddAgentGroup(Group, group => group
                    .Named(new() { ["en"] = "Project management", ["nl"] = "Projectbeheer" }, fallbackLanguage: "en")
                    .Describe(new() { ["en"] = "Agents that keep the projects moving.", ["nl"] = "Agents die de projecten op gang houden." }))
                .AddAgentGroup(Reporting, group => group.Named(new() { ["en"] = "Reporting" }, fallbackLanguage: "en"))
                .AddAgentGroup(Restricted, group => group.Named(new() { ["en"] = "Follow-up only" }, fallbackLanguage: "en"))
                .AddAgent(AssistantTestData.Agent(FollowUp))
                .AddAgent(AssistantTestData.Agent(Statistics))
                .AddAgent(AssistantTestData.Agent(Regional))
                .ConfigureAgent(ProjectAgent, run => run.WorksOn("project"))
                .ConfigureAgent(FollowUp, run => run
                    .WorksOn("project")
                    .WorksOnApplication()
                    .ViewPolicy(ViewPolicy)
                    .InGroup(Group)
                    .InGroup(Restricted))
                .ConfigureAgent(Statistics, run => run.WorksOn("project").WorksOnApplication().InGroup(Group).InGroup(Reporting))
                .ConfigureAgent(Regional, run => run.WorksOnApplication().PerDivision().InGroup(Group)),
            provider: provider);
    }

    private static async Task<AssistantThread> ThreadAtAsync(AssistantWebApplication app, string agentId, NhAiSubjectRef subject)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var info = (await services.GetRequiredService<NhAssistantSubjectAccessService>().DescribeAsync(subject, CancellationToken.None))!;
        var agent = (await services.GetRequiredService<NhAssistantAgentCatalog>().FindAsync(agentId, includeDisabled: false, CancellationToken.None))!;
        return (await services.GetRequiredService<NhAssistantThreadService>().GetOrCreateAsync(agent, info, CancellationToken.None)).Data!;
    }

    /// <summary>
    /// Stores a completed run of the agent on the subject that answered <paramref name="result"/> and
    /// ended 30 seconds after <paramref name="createdAt"/>.
    /// </summary>
    private static async Task<Guid> SeedRunAsync(
        AssistantWebApplication app,
        string agentId,
        NhAiSubjectRef subject,
        DateTimeOffset createdAt,
        string result)
    {
        var thread = await ThreadAtAsync(app, agentId, subject);
        var run = new AssistantRun
        {
            Id = Guid.NewGuid(),
            ConversationId = thread.ConversationId,
            TriggerId = NhAssistantTriggerIds.Manual,
            AccountableOwnerId = thread.AccountableOwnerId,
            Status = NhAssistantRunStatuses.Completed,
            AgentVersion = 1,
            Summary = "Ended completed.",
            Result = result,
            CreatedAt = createdAt,
            StartedAt = createdAt,
            EndedAt = createdAt.AddSeconds(30),
            ConcurrencyStamp = Guid.NewGuid()
        };
        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<NhAssistantRunStore>().AddRunAsync(run, CancellationToken.None);
        return run.Id;
    }

    private static async Task<Guid> AddProposalAsync(
        AssistantWebApplication app,
        Guid conversationId,
        Guid runId,
        DateTimeOffset createdAt,
        Guid? placedFrom = null)
    {
        await using var scope = app.Services.CreateAsyncScope();
        await using var context = scope.ServiceProvider.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        var approval = new AssistantApproval
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
            CreatedAt = createdAt,
            ExpiresAt = createdAt.AddDays(1),
            RunId = runId,
            PlacedFromConversationId = placedFrom,
            Mode = NhAssistantApprovalModes.Deferred,
            ConcurrencyStamp = Guid.NewGuid()
        };
        context.Approvals.Add(approval);
        await context.SaveChangesAsync();
        return approval.Id;
    }

    private static async Task<Guid> AddArtifactAsync(AssistantWebApplication app, Guid conversationId, Guid runId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var bytes = Encoding.UTF8.GetBytes("Five projects are open.");
        var artifact = await scope.ServiceProvider.GetRequiredService<NhAssistantArtifactStore>().AddVersionAsync(
            new AssistantArtifact
            {
                Id = Guid.NewGuid(),
                ConversationId = conversationId,
                RunId = runId,
                Name = "weekly-report",
                ContentType = "text/plain",
                SizeBytes = bytes.Length,
                Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)),
                Content = bytes,
                CreatedAt = DateTimeOffset.UtcNow
            },
            CancellationToken.None);
        return artifact.Id;
    }

    private static async Task<(string GroupId, bool IsMember)[]> ChoicesAsync(AssistantWebApplication app, string agentId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<NhAssistantBoardStore>().ListMembershipsOfAgentAsync(agentId, CancellationToken.None))
            .Select(choice => (choice.GroupId, choice.IsMember))
            .ToArray();
    }

    private static async Task<int> VisitCountAsync(AssistantWebApplication app, string actorId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        await using var context = scope.ServiceProvider.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        return context.BoardVisits.Count(visit => visit.ActorId == actorId);
    }

    private static async Task<Dictionary<string, JsonElement>> AdminAgentsAsync(HttpClient admin)
    {
        var agents = await ReadJsonAsync(await admin.GetAsync("/api/assistant/admin/agents"));
        return agents.EnumerateArray().ToDictionary(agent => agent.GetProperty("id").GetString()!, StringComparer.Ordinal);
    }

    private static string[] Strings(JsonElement element, string property)
    {
        return element.GetProperty(property).EnumerateArray().Select(item => item.GetString()!).ToArray();
    }

    private static string[] AgentIds(JsonElement board)
    {
        return board.GetProperty("agents").EnumerateArray().Select(agent => agent.GetProperty("agentId").GetString()!).ToArray();
    }

    private static JsonElement Agent(JsonElement board, string agentId)
    {
        return board.GetProperty("agents").EnumerateArray().Single(agent => agent.GetProperty("agentId").GetString() == agentId);
    }

    private static async Task AssertBoardNotFoundAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(NhAssistantErrorCodes.BoardNotFound, (await ReadJsonAsync(response, HttpStatusCode.NotFound)).GetProperty("code").GetString());
    }

    private static async Task AssertFieldErrorAsync(HttpResponseMessage response, string error)
    {
        using (response)
        {
            var body = await ReadJsonAsync(response, HttpStatusCode.BadRequest);
            Assert.Equal(error, Assert.Single(body.GetProperty("errors").GetProperty("groups").EnumerateArray()).GetString());
        }
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        Assert.Equal(expected, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    /// <summary>
    /// The test's projects: everyone reads them.
    /// </summary>
    private sealed class BoardProjects;

    private sealed class BoardProjectResolver : INhAiSubjectAccessResolver
    {
        public string SubjectType => "project";

        public ValueTask<NhAiSubjectInfo?> DescribeAsync(NhAiSubjectRef subject, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<NhAiSubjectInfo?>(new NhAiSubjectInfo(subject, NhAiSubjectInfo.CreateScopeKey(null, null)) { DisplayName = "Project " + subject.Id });
        }

        public ValueTask<NhAiSubjectAccess> ResolveAsync(NhAiSubjectRef subject, string actorId, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(new NhAiSubjectAccess(NhAiSubjectRole.Reader));
        }

        public async IAsyncEnumerable<NhAiSubjectMember> ListMembersAsync(
            NhAiSubjectRef subject,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    /// <summary>
    /// The people as headless runs and the whole application see them: the administrator administers,
    /// the manager administers and passes the results' view policy, the viewer passes that view policy,
    /// the reader only uses the assistant.
    /// </summary>
    private sealed class BoardPrincipals : INhAiPrincipalFactory
    {
        public ValueTask<ClaimsPrincipal?> CreateAsync(string actorId, NhAiSubjectInfo subject, CancellationToken cancellationToken = default)
        {
            var permissions = actorId switch
            {
                Admin => new[] { "assistant", "assistant-admin" },
                Manager => new[] { "assistant", "assistant-admin", ViewPolicy },
                Viewer => new[] { "assistant", ViewPolicy },
                Reader => new[] { "assistant" },
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
