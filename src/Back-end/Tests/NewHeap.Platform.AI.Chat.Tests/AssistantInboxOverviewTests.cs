using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Actors;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// The overview of the approval inbox: proposals grouped by the parent they recorded, contiguous across
/// pages with their totals, together with the division and run filters, and what is new since a
/// person's last visit of the inbox per scope; on both relational providers.
/// </summary>
public sealed class AssistantInboxOverviewTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string ProjectAgent = "project-assistant";
    private const string Group = "project-management";
    private const string Approver = "reader-1";
    private const string Outsider = "outsider-1";

    // Runs whose ids order the same on both providers: they differ in the last byte only.
    private static readonly Guid FirstRun = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid SecondRun = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly Guid ThirdRun = Guid.Parse("00000000-0000-0000-0000-000000000003");
    private static readonly Guid FourthRun = Guid.Parse("00000000-0000-0000-0000-000000000004");
    private static readonly Guid FifthRun = Guid.Parse("00000000-0000-0000-0000-000000000005");

    [Fact]
    public void The_platform_keeps_group_ids_that_start_with_nh_for_itself()
    {
        var services = new ServiceCollection();

        var refused = Assert.Throws<ArgumentException>(() => services.AddNewHeapAssistant(assistant => assistant
            .UsePostgreSql("Host=localhost;Database=nh_assistant_inbox", options => options.Schema = "nhai")
            .UseChatProfile("project-chat")
            .AddAgent(AssistantTestData.Agent() with { ProfileName = "project-chat" })
            .AddAgentGroup(
                NhAssistantAgentGroupDefinition.InboxVisitId,
                group => group.Named(new() { ["en"] = "Inbox" }, fallbackLanguage: "en"))));

        Assert.Contains("reserved for the platform", refused.Message);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task The_grouped_inbox_keeps_parents_subjects_and_runs_together_across_pages_with_their_totals(AssistantTestProvider provider)
    {
        await using var app = await StartAsync(provider);
        var now = DateTimeOffset.UtcNow;
        // North: project n1 with its own proposal and those of its tasks n1-a and n1-b; project n2.
        // South: project s1 and its task s1-a.
        var a = await ProposeAsync(app, ProjectRef("n1"), FirstRun, now.AddHours(1));
        var b = await ProposeAsync(app, TaskRef("n1-a"), SecondRun, now.AddHours(2));
        var c = await ProposeAsync(app, TaskRef("n1-a"), ThirdRun, now.AddMinutes(30));
        var d = await ProposeAsync(app, TaskRef("n1-a"), SecondRun, now.AddHours(3));
        var e = await ProposeAsync(app, TaskRef("n1-b"), SecondRun, now.AddHours(4));
        var f = await ProposeAsync(app, ProjectRef("n2"), FourthRun, now.AddMinutes(10));
        var g = await ProposeAsync(app, TaskRef("s1-a"), FifthRun, now.AddMinutes(20));
        var h = await ProposeAsync(app, ProjectRef("s1"), FifthRun, now.AddHours(5));
        using var approver = app.CreateClient(Approver);

        // The flat inbox keeps its order: the proposals that expire first on top, without groups.
        var flat = await ReadJsonAsync(await approver.GetAsync("/api/assistant/approvals/inbox?itemsPerPage=20"));
        Assert.Equal([f, g, c, a, b, d, e, h], Ids(flat));
        Assert.Equal(JsonValueKind.Null, flat.GetProperty("groups").ValueKind);

        // Grouped: groups by their first expiry (n2, s1, n1); a group's own subject first, then its other
        // subjects by their first expiry; one run's proposals together, then by expiry.
        var all = await ReadJsonAsync(await approver.GetAsync("/api/assistant/approvals/inbox?grouped=true&itemsPerPage=20"));
        Assert.Equal([f, h, g, a, b, d, c, e], Ids(all));
        Assert.Equal(8, all.GetProperty("total").GetInt32());
        Assert.Equal(
            [("project", "n2", "Project n2", 1, 1), ("project", "s1", "Project s1", 2, 2), ("project", "n1", "Project n1", 5, 5)],
            Groups(all).Select(group => (
                group.GetProperty("subjectType").GetString(),
                group.GetProperty("subjectId").GetString(),
                group.GetProperty("displayName").GetString(),
                group.GetProperty("count").GetInt32(),
                group.GetProperty("total").GetInt32())));
        var n1 = Groups(all)[2];
        Assert.Equal(("Project", "/projects/n1"), (n1.GetProperty("subjectTypeName").GetString(), n1.GetProperty("subjectLink").GetString()));
        Assert.Equal(
            [("project", "n1", 1, 1), ("project-task", "n1-a", 3, 3), ("project-task", "n1-b", 1, 1)],
            Subjects(n1).Select(subject => (
                subject.GetProperty("subjectType").GetString(),
                subject.GetProperty("subjectId").GetString(),
                subject.GetProperty("count").GetInt32(),
                subject.GetProperty("total").GetInt32())));
        // Every proposal carries the parent it recorded, named as the application names the type.
        var task = Item(all, b).GetProperty("parent");
        Assert.Equal(("project", "n1", "Project n1", "Project", "/projects/n1"), (
            task.GetProperty("subjectType").GetString(),
            task.GetProperty("subjectId").GetString(),
            task.GetProperty("displayName").GetString(),
            task.GetProperty("subjectTypeName").GetString(),
            task.GetProperty("subjectLink").GetString()));
        Assert.Equal(JsonValueKind.Null, Item(all, a).GetProperty("parent").ValueKind);

        // Paged by items: a group that goes on says so on both pages, with its total.
        var first = await ReadJsonAsync(await approver.GetAsync("/api/assistant/approvals/inbox?grouped=true&itemsPerPage=3&page=1"));
        Assert.Equal([f, h, g], Ids(first));
        Assert.Equal([("n2", 1, 1, false, false), ("s1", 2, 2, false, false)], Shape(first));
        var second = await ReadJsonAsync(await approver.GetAsync("/api/assistant/approvals/inbox?grouped=true&itemsPerPage=3&page=2"));
        Assert.Equal([a, b, d], Ids(second));
        Assert.Equal([("n1", 3, 5, false, true)], Shape(second));
        Assert.Equal(
            [("n1", 1, 1, false, false), ("n1-a", 2, 3, false, true)],
            SubjectShape(Groups(second)[0]));
        var third = await ReadJsonAsync(await approver.GetAsync("/api/assistant/approvals/inbox?grouped=true&itemsPerPage=3&page=3"));
        Assert.Equal([c, e], Ids(third));
        Assert.Equal([("n1", 2, 5, true, false)], Shape(third));
        Assert.Equal(
            [("n1-a", 1, 3, true, false), ("n1-b", 1, 1, false, false)],
            SubjectShape(Groups(third)[0]));
        var beyond = await ReadJsonAsync(await approver.GetAsync("/api/assistant/approvals/inbox?grouped=true&itemsPerPage=3&page=4"));
        Assert.Empty(Ids(beyond));
        Assert.Empty(Groups(beyond));

        // The division filter keeps the groups of that division, with totals of what it shows.
        var south = await ReadJsonAsync(await approver.GetAsync($"/api/assistant/approvals/inbox?grouped=true&divisionId={AssistantSystemSubjectTests.South}"));
        Assert.Equal([h, g], Ids(south));
        Assert.Equal([("s1", 2, 2, false, false)], Shape(south));
        var north = await ReadJsonAsync(await approver.GetAsync($"/api/assistant/approvals/inbox?grouped=true&itemsPerPage=4&divisionId={AssistantSystemSubjectTests.North}"));
        Assert.Equal([f, a, b, d], Ids(north));
        Assert.Equal([("n2", 1, 1, false, false), ("n1", 3, 5, false, true)], Shape(north));

        // The run filter groups the run's proposals the same way.
        var run = await ReadJsonAsync(await approver.GetAsync($"/api/assistant/approvals/inbox?grouped=true&runId={SecondRun}"));
        Assert.Equal([b, d, e], Ids(run));
        Assert.Equal([("n1", 3, 3, false, false)], Shape(run));
        Assert.Equal([("n1-a", 2, 2, false, false), ("n1-b", 1, 1, false, false)], SubjectShape(Groups(run)[0]));

        // Grouping lists only what the caller may decide.
        using var outsider = app.CreateClient(Outsider);
        var none = await ReadJsonAsync(await outsider.GetAsync("/api/assistant/approvals/inbox?grouped=true"));
        Assert.Equal(0, none.GetProperty("total").GetInt32());
        Assert.Empty(Groups(none));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task The_inbox_marks_what_is_new_since_the_last_visit_per_scope(AssistantTestProvider provider)
    {
        await using var app = await StartAsync(provider);
        var now = DateTimeOffset.UtcNow.AddMinutes(-5);
        var older = await ProposeAsync(app, ProjectRef("n1"), FirstRun, now.AddHours(1), createdAt: now);
        var southern = await ProposeAsync(app, ProjectRef("s1"), SecondRun, now.AddHours(2), createdAt: now);
        using var approver = app.CreateClient(Approver);

        // Before the first visit everything is new.
        var inbox = await ReadJsonAsync(await approver.GetAsync("/api/assistant/approvals/inbox"));
        Assert.Equal(("global", JsonValueKind.Null), (inbox.GetProperty("scopeKey").GetString(), inbox.GetProperty("lastVisitedAt").ValueKind));
        Assert.Equal([older, southern], NewIds(inbox));

        // A visit marks what came before as seen; what is proposed later is new.
        var visit = await ReadJsonAsync(await approver.PostAsync("/api/assistant/approvals/inbox/visit", null));
        Assert.Equal("global", visit.GetProperty("scopeKey").GetString());
        var visitedAt = visit.GetProperty("visitedAt").GetDateTimeOffset();
        inbox = await ReadJsonAsync(await approver.GetAsync("/api/assistant/approvals/inbox"));
        Assert.Equal(visitedAt, inbox.GetProperty("lastVisitedAt").GetDateTimeOffset());
        Assert.Empty(NewIds(inbox));
        // Made a microsecond after the visit, which both providers store exactly.
        var later = await ProposeAsync(app, TaskRef("n1-a"), ThirdRun, now.AddHours(3), createdAt: visitedAt.AddTicks(10));
        inbox = await ReadJsonAsync(await approver.GetAsync("/api/assistant/approvals/inbox?grouped=true"));
        Assert.Equal([later], NewIds(inbox));

        // The inbox of a division keeps its own visits.
        var northPath = $"/api/assistant/approvals/inbox?divisionId={AssistantSystemSubjectTests.North}";
        var northInbox = await ReadJsonAsync(await approver.GetAsync(northPath));
        Assert.Equal(($"division:{AssistantSystemSubjectTests.North:N}", JsonValueKind.Null), (
            northInbox.GetProperty("scopeKey").GetString(),
            northInbox.GetProperty("lastVisitedAt").ValueKind));
        Assert.Equal([older, later], NewIds(northInbox));
        var northVisit = await ReadJsonAsync(await approver.PostAsync($"/api/assistant/approvals/inbox/visit?divisionId={AssistantSystemSubjectTests.North}", null));
        Assert.Equal($"division:{AssistantSystemSubjectTests.North:N}", northVisit.GetProperty("scopeKey").GetString());
        Assert.Empty(NewIds(await ReadJsonAsync(await approver.GetAsync(northPath))));
        Assert.Equal([later], NewIds(await ReadJsonAsync(await approver.GetAsync("/api/assistant/approvals/inbox"))));

        // A division the person may not work in and an unknown one are refused, and store nothing.
        foreach (var division in new[] { AssistantSystemSubjectTests.South, Guid.NewGuid() })
        {
            using var refused = await approver.PostAsync($"/api/assistant/approvals/inbox/visit?divisionId={division}", null);
            Assert.Equal(
                NhAssistantErrorCodes.SubjectNotFound,
                (await ReadJsonAsync(refused, HttpStatusCode.NotFound)).GetProperty("code").GetString());
        }
        using (var malformed = await approver.PostAsync("/api/assistant/approvals/inbox/visit?divisionId=north", null))
        {
            Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        }
        Assert.Equal(2, await InboxVisitCountAsync(app, Approver));

        // Visits are per person; the run maintenance keeps the inbox's visits while they are recent,
        // although no group carries their id.
        using var outsider = app.CreateClient(Outsider);
        Assert.Equal(JsonValueKind.Null, (await ReadJsonAsync(await outsider.GetAsync("/api/assistant/approvals/inbox"))).GetProperty("lastVisitedAt").ValueKind);
        var maintenance = app.Services.GetServices<IHostedService>().OfType<NhAssistantRunMaintenance>().Single();
        await using (var scope = app.Services.CreateAsyncScope())
        {
            await maintenance.RemoveStaleBoardVisitsAsync(
                scope.ServiceProvider.GetRequiredService<NhAssistantBoardStore>(),
                DateTimeOffset.UtcNow,
                CancellationToken.None);
        }
        Assert.Equal(2, await InboxVisitCountAsync(app, Approver));
    }

    private static NhAiSubjectRef ProjectRef(string id)
    {
        return new NhAiSubjectRef("project", id);
    }

    private static NhAiSubjectRef TaskRef(string id)
    {
        return new NhAiSubjectRef("project-task", id);
    }

    private Task<AssistantWebApplication> StartAsync(AssistantTestProvider provider)
    {
        return AssistantWebApplication.StartAsync(
            database,
            new NhAiScriptedChatClient(),
            configure: services =>
            {
                services.AddNewHeapDivisionDirectory<TestDivisionDirectory>();
                services.AddNewHeapDivisionAccess<TestDivisionAccess>();
                // Proposals are seeded here, not made by runs.
                services.Remove(services.Single(descriptor => descriptor.ImplementationType == typeof(NhAssistantStartupValidator)));
            },
            assistantBuilder: assistant => assistant
                .AddSubjectType<InboxProjectResolver>(type => type
                    .Named(new() { ["en"] = "Project" }, fallbackLanguage: "en")
                    .OpenAt("/projects/{id}"))
                .AddSubjectType<InboxTaskResolver>(type => type.Named(new() { ["en"] = "Task" }, fallbackLanguage: "en"))
                .AddAgentGroup(Group, group => group.Named(new() { ["en"] = "Project management" }, fallbackLanguage: "en"))
                .ConfigureAgent(ProjectAgent, run => run.WorksOn("project", "project-task").InGroup(Group)),
            provider: provider);
    }

    /// <summary>
    /// Stores a pending proposal of the run in the agent's thread at the subject, with the parent its
    /// resolver names, and lists the approver in the subject's member snapshot.
    /// </summary>
    private static async Task<Guid> ProposeAsync(
        AssistantWebApplication app,
        NhAiSubjectRef subject,
        Guid runId,
        DateTimeOffset expiresAt,
        DateTimeOffset? createdAt = null)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var info = (await services.GetRequiredService<NhAssistantSubjectAccessService>().DescribeAsync(subject, CancellationToken.None))!;
        var agent = (await services.GetRequiredService<NhAssistantAgentCatalog>().FindAsync(ProjectAgent, includeDisabled: false, CancellationToken.None))!;
        var thread = (await services.GetRequiredService<NhAssistantThreadService>().GetOrCreateAsync(agent, info, CancellationToken.None)).Data!;
        await using var context = services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        if (!context.SubjectMembers.Any(member => member.ScopeKey == info.ScopeKey
                && member.SubjectType == subject.Type
                && member.SubjectId == subject.Id
                && member.ActorId == Approver))
        {
            context.SubjectMembers.Add(new AssistantSubjectMember
            {
                ScopeKey = info.ScopeKey,
                SubjectType = subject.Type,
                SubjectId = subject.Id,
                ActorId = Approver,
                Role = NhAiSubjectRole.Approver,
                DisplayName = Approver,
                SnapshotAt = DateTimeOffset.UtcNow
            });
        }

        var approval = new AssistantApproval
        {
            Id = Guid.NewGuid(),
            ConversationId = thread.ConversationId,
            TurnId = Guid.NewGuid(),
            ToolInvocationId = Guid.NewGuid(),
            ProposalId = Guid.NewGuid(),
            ProposalHash = "sha256:" + Guid.NewGuid().ToString("N"),
            ProposalJson = "{}",
            ToolId = "projects.complete",
            Summary = "Complete it.",
            ArgumentsPreview = "{}",
            Status = NhAssistantApprovalStatuses.Pending,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow.AddMinutes(-1),
            ExpiresAt = expiresAt,
            RunId = runId,
            Mode = NhAssistantApprovalModes.Deferred,
            // As the interceptor records it from the subject's description.
            ParentSubjectType = info.Parent?.Subject.Type,
            ParentSubjectId = info.Parent?.Subject.Id,
            ParentDisplayName = info.Parent?.DisplayName,
            ConcurrencyStamp = Guid.NewGuid()
        };
        context.Approvals.Add(approval);
        await context.SaveChangesAsync();
        return approval.Id;
    }

    private static async Task<int> InboxVisitCountAsync(AssistantWebApplication app, string actorId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        await using var context = scope.ServiceProvider.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        return context.BoardVisits.Count(visit => visit.ActorId == actorId && visit.GroupId == NhAssistantAgentGroupDefinition.InboxVisitId);
    }

    private static Guid[] Ids(JsonElement inbox)
    {
        return inbox.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("approvalId").GetGuid()).ToArray();
    }

    private static Guid[] NewIds(JsonElement inbox)
    {
        var ids = Ids(inbox);
        // In the order of the page.
        var fresh = inbox.GetProperty("newApprovalIds").EnumerateArray().Select(id => id.GetGuid()).ToHashSet();
        return ids.Where(fresh.Contains).ToArray();
    }

    private static JsonElement Item(JsonElement inbox, Guid approvalId)
    {
        return inbox.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("approvalId").GetGuid() == approvalId);
    }

    private static JsonElement[] Groups(JsonElement inbox)
    {
        return inbox.GetProperty("groups").EnumerateArray().ToArray();
    }

    private static JsonElement[] Subjects(JsonElement group)
    {
        return group.GetProperty("subjects").EnumerateArray().ToArray();
    }

    private static (string?, int, int, bool, bool)[] Shape(JsonElement inbox)
    {
        return Groups(inbox).Select(group => (
            group.GetProperty("subjectId").GetString(),
            group.GetProperty("count").GetInt32(),
            group.GetProperty("total").GetInt32(),
            group.GetProperty("continuesFromPreviousPage").GetBoolean(),
            group.GetProperty("continuesOnNextPage").GetBoolean())).ToArray();
    }

    private static (string?, int, int, bool, bool)[] SubjectShape(JsonElement group)
    {
        return Subjects(group).Select(subject => (
            subject.GetProperty("subjectId").GetString(),
            subject.GetProperty("count").GetInt32(),
            subject.GetProperty("total").GetInt32(),
            subject.GetProperty("continuesFromPreviousPage").GetBoolean(),
            subject.GetProperty("continuesOnNextPage").GetBoolean())).ToArray();
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        Assert.Equal(expected, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    /// <summary>
    /// The division of a test subject: ids that start with <c>n</c> are in North, others in South.
    /// </summary>
    private static Guid DivisionOf(string id)
    {
        return id.StartsWith('n') ? AssistantSystemSubjectTests.North : AssistantSystemSubjectTests.South;
    }

    /// <summary>
    /// Projects without a parent, each in its division; everyone reads them.
    /// </summary>
    private sealed class InboxProjectResolver : INhAiSubjectAccessResolver
    {
        public string SubjectType => "project";

        public ValueTask<NhAiSubjectInfo?> DescribeAsync(NhAiSubjectRef subject, CancellationToken cancellationToken = default)
        {
            var division = DivisionOf(subject.Id);
            return ValueTask.FromResult<NhAiSubjectInfo?>(new NhAiSubjectInfo(subject, NhAiSubjectInfo.CreateScopeKey(null, division))
            {
                DivisionId = division,
                DisplayName = "Project " + subject.Id
            });
        }

        public ValueTask<NhAiSubjectAccess> ResolveAsync(NhAiSubjectRef subject, string actorId, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(new NhAiSubjectAccess(actorId == Approver ? NhAiSubjectRole.Approver : NhAiSubjectRole.Reader));
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
    /// Tasks <c>{project}-{letter}</c>, whose parent is their project in the project's division.
    /// </summary>
    private sealed class InboxTaskResolver : INhAiSubjectAccessResolver
    {
        public string SubjectType => "project-task";

        public ValueTask<NhAiSubjectInfo?> DescribeAsync(NhAiSubjectRef subject, CancellationToken cancellationToken = default)
        {
            var project = subject.Id[..subject.Id.IndexOf('-')];
            var division = DivisionOf(subject.Id);
            return ValueTask.FromResult<NhAiSubjectInfo?>(new NhAiSubjectInfo(subject, NhAiSubjectInfo.CreateScopeKey(null, division))
            {
                DivisionId = division,
                DisplayName = "Task " + subject.Id,
                Parent = new NhAiSubjectParent(new NhAiSubjectRef("project", project), "Project " + project)
            });
        }

        public ValueTask<NhAiSubjectAccess> ResolveAsync(NhAiSubjectRef subject, string actorId, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(new NhAiSubjectAccess(actorId == Approver ? NhAiSubjectRole.Approver : NhAiSubjectRole.Reader));
        }

        public async IAsyncEnumerable<NhAiSubjectMember> ListMembersAsync(
            NhAiSubjectRef subject,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
