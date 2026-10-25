using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Json;
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
/// LS14: the approval inbox lists proposals on their way by status (<c>pending</c> by default,
/// <c>awaiting-application</c> until the report falls due and <c>execution-unknown</c>), flat and
/// grouped, and <see cref="INhAssistantProposalCounts"/> counts them per status with the inbox's own
/// visibility: the member snapshot, the run's initiator and the whole application's live approvers.
/// On both relational providers.
/// </summary>
public sealed class AssistantInboxStatusTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string ProjectAgent = "project-assistant";
    private const string AppAgent = "results-follow-up";
    private const string SystemReadPolicy = "inbox.system.read";
    private const string SystemApprovePolicy = "inbox.system.approve";
    private const string Approver = "reader-1";
    private const string Initiator = "outsider-1";
    private const string SystemApprover = "admin-1";

    private static readonly Guid FirstRun = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid SecondRun = Guid.Parse("00000000-0000-0000-0000-000000000002");

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task The_inbox_lists_each_status_with_the_same_visibility_and_the_counts_follow_it(AssistantTestProvider provider)
    {
        await using var app = await StartAsync(provider);
        var now = DateTimeOffset.UtcNow;
        var project = new NhAiSubjectRef("project", "p1");
        var pending = await ProposeAsync(app, project, FirstRun, NhAssistantApprovalStatuses.Pending, now.AddHours(1));
        var dueLater = await ProposeAsync(app, project, FirstRun, NhAssistantApprovalStatuses.AwaitingApplication, now.AddMinutes(-10), dueAt: now.AddHours(3));
        var dueFirst = await ProposeAsync(app, project, SecondRun, NhAssistantApprovalStatuses.AwaitingApplication, now.AddMinutes(-10), dueAt: now.AddHours(2), initiator: Initiator);
        // Its report is overdue: the run maintenance makes it uncertain next, so it waits for nothing.
        await ProposeAsync(app, project, FirstRun, NhAssistantApprovalStatuses.AwaitingApplication, now.AddHours(1), dueAt: now.AddMinutes(-1));
        var uncertain = await ProposeAsync(app, project, FirstRun, NhAssistantApprovalStatuses.ExecutionUnknown, now.AddMinutes(-30), decidedAt: now.AddMinutes(-20));
        var older = await ProposeAsync(app, project, SecondRun, NhAssistantApprovalStatuses.ExecutionUnknown, now.AddMinutes(-30), decidedAt: now.AddMinutes(-40));
        // Executed proposals are on their way no longer.
        await ProposeAsync(app, project, FirstRun, NhAssistantApprovalStatuses.Executed, now.AddMinutes(-30));
        var system = await ProposeAsync(app, NhAiSystemSubjects.Global, FirstRun, NhAssistantApprovalStatuses.AwaitingApplication, now.AddMinutes(-5), dueAt: now.AddHours(1), agentId: AppAgent);
        using var approver = app.CreateClient(Approver);

        // Pending stays the default, and the page says which status it lists.
        var inbox = await ReadJsonAsync(await approver.GetAsync("/api/assistant/approvals/inbox"));
        Assert.Equal("pending", inbox.GetProperty("status").GetString());
        Assert.Equal([pending], Ids(inbox));

        // Waiting for the application, by report deadline; flat and grouped by subject and run.
        var awaiting = await ReadJsonAsync(await approver.GetAsync("/api/assistant/approvals/inbox?status=awaiting-application"));
        Assert.Equal(("awaiting-application", 2), (awaiting.GetProperty("status").GetString(), awaiting.GetProperty("total").GetInt32()));
        Assert.Equal([dueFirst, dueLater], Ids(awaiting));
        var dueFirstAt = Item(awaiting, dueFirst).GetProperty("executionReportDueAt").GetDateTimeOffset();
        Assert.True((dueFirstAt - now.AddHours(2)).Duration() < TimeSpan.FromMilliseconds(1));
        var grouped = await ReadJsonAsync(await approver.GetAsync("/api/assistant/approvals/inbox?status=awaiting-application&grouped=true"));
        Assert.Equal([dueLater, dueFirst], Ids(grouped));
        Assert.Equal(2, Assert.Single(grouped.GetProperty("groups").EnumerateArray()).GetProperty("total").GetInt32());

        // Uncertain executions until an approver resolves them, by decision.
        var unknown = await ReadJsonAsync(await approver.GetAsync("/api/assistant/approvals/inbox?status=execution-unknown"));
        Assert.Equal([older, uncertain], Ids(unknown));

        // Any other status is refused.
        foreach (var other in new[] { "executed", "approved", "Pending", "" })
        {
            using var refused = await approver.GetAsync("/api/assistant/approvals/inbox?status=" + other);
            if (other.Length == 0)
            {
                // An empty value is no value: the default.
                Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
                continue;
            }

            Assert.Equal(NhAssistantErrorCodes.MessageInvalid, (await ReadJsonAsync(refused, HttpStatusCode.BadRequest)).GetProperty("code").GetString());
        }

        // The initiator of a run sees its proposal in the run's own thread; the whole application's
        // approver sees its proposal and none of the project's.
        using var initiator = app.CreateClient(Initiator);
        Assert.Equal([dueFirst], Ids(await ReadJsonAsync(await initiator.GetAsync("/api/assistant/approvals/inbox?status=awaiting-application"))));
        using var systemApprover = app.CreateClient(SystemApprover);
        Assert.Equal([system], Ids(await ReadJsonAsync(await systemApprover.GetAsync("/api/assistant/approvals/inbox?status=awaiting-application"))));
        Assert.Empty(Ids(await ReadJsonAsync(await systemApprover.GetAsync("/api/assistant/approvals/inbox?status=execution-unknown"))));

        // The counts are the inbox's totals per status, ids and counts only.
        Assert.Equal(
            new Dictionary<string, int> { ["pending"] = 1, ["awaiting-application"] = 2, ["execution-unknown"] = 2 },
            await CountAsync(app, Approver, NhAssistantInboxStatuses.All));
        Assert.Equal(
            new Dictionary<string, int> { ["awaiting-application"] = 1, ["pending"] = 0 },
            await CountAsync(app, Initiator, [NhAssistantInboxStatuses.AwaitingApplication, NhAssistantInboxStatuses.Pending, NhAssistantInboxStatuses.AwaitingApplication]));
        Assert.Equal(
            new Dictionary<string, int> { ["awaiting-application"] = 1, ["execution-unknown"] = 0 },
            await CountAsync(app, SystemApprover, [NhAssistantInboxStatuses.AwaitingApplication, NhAssistantInboxStatuses.ExecutionUnknown]));
        // A person bound to another tenant takes no part in the whole application.
        Assert.Equal(0, (await CountAsync(app, SystemApprover, [NhAssistantInboxStatuses.AwaitingApplication], tenantId: "tenant-b"))["awaiting-application"]);
        Assert.Empty(await CountAsync(app, Approver, []));
        await Assert.ThrowsAsync<ArgumentException>(() => CountAsync(app, Approver, ["executed"]));
        await Assert.ThrowsAsync<ArgumentException>(() => CountAsync(app, " ", [NhAssistantInboxStatuses.Pending]));
    }

    private Task<AssistantWebApplication> StartAsync(AssistantTestProvider provider)
    {
        return AssistantWebApplication.StartAsync(
            database,
            new NhAiScriptedChatClient(),
            configure: services =>
            {
                services.AddSingleton<SystemSubjectPrincipals>();
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory>(provider => provider.GetRequiredService<SystemSubjectPrincipals>()));
                services.AddAuthorization(options =>
                {
                    options.AddPolicy(SystemReadPolicy, policy => policy.RequireClaim("permission", "assistant"));
                    options.AddPolicy(SystemApprovePolicy, policy => policy.RequireClaim("permission", "app.project.manage"));
                });
                // Proposals are seeded here, not made by runs.
                services.Remove(services.Single(descriptor => descriptor.ImplementationType == typeof(NhAssistantStartupValidator)));
            },
            assistantBuilder: assistant => assistant
                .AddSubjectType<StatusProjectResolver>(type => type.Named(new() { ["en"] = "Project" }, fallbackLanguage: "en"))
                .UseSystemSubject(system => system.ReadPolicy(SystemReadPolicy).ApprovePolicy(SystemApprovePolicy))
                .AddAgent(AssistantTestData.Agent(AppAgent))
                .ConfigureAgent(ProjectAgent, run => run.WorksOn("project"))
                .ConfigureAgent(AppAgent, run => run.WorksOnApplication()),
            provider: provider);
    }

    /// <summary>
    /// Stores a deferred proposal of a run in the agent's thread at the subject, in
    /// <paramref name="status"/>, and lists the approver in a project's member snapshot.
    /// </summary>
    private static async Task<Guid> ProposeAsync(
        AssistantWebApplication app,
        NhAiSubjectRef subject,
        Guid runId,
        string status,
        DateTimeOffset expiresAt,
        DateTimeOffset? dueAt = null,
        DateTimeOffset? decidedAt = null,
        string? initiator = null,
        string agentId = ProjectAgent)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var info = (await services.GetRequiredService<NhAssistantSubjectAccessService>().DescribeAsync(subject, CancellationToken.None))!;
        var agent = (await services.GetRequiredService<NhAssistantAgentCatalog>().FindAsync(agentId, includeDisabled: false, CancellationToken.None))!;
        var thread = (await services.GetRequiredService<NhAssistantThreadService>().GetOrCreateAsync(agent, info, CancellationToken.None)).Data!;
        await using var context = services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        if (!NhAiSystemSubjects.IsSystem(subject)
            && !context.SubjectMembers.Any(member => member.ScopeKey == info.ScopeKey
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

        var decided = status != NhAssistantApprovalStatuses.Pending;
        var approval = new AssistantApproval
        {
            Id = Guid.NewGuid(),
            ConversationId = thread.ConversationId,
            TurnId = Guid.NewGuid(),
            ToolInvocationId = Guid.NewGuid(),
            ProposalId = Guid.NewGuid(),
            ProposalHash = "sha256:" + Guid.NewGuid().ToString("N"),
            ProposalJson = "{}",
            ToolId = "projects.order-hardware",
            Summary = "Order hardware.",
            ArgumentsPreview = "{}",
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-1),
            ExpiresAt = expiresAt,
            RunId = runId,
            InitiatorActorId = initiator,
            Mode = NhAssistantApprovalModes.Deferred,
            DecidedByActorId = decided ? Approver : null,
            DecidedAt = decided ? decidedAt ?? DateTimeOffset.UtcNow.AddMinutes(-50) : null,
            ExecutionAttempts = dueAt is null ? 0 : 1,
            ExecutionReportDueAt = dueAt,
            ConcurrencyStamp = Guid.NewGuid()
        };
        context.Approvals.Add(approval);
        await context.SaveChangesAsync();
        return approval.Id;
    }

    private static async Task<IReadOnlyDictionary<string, int>> CountAsync(
        AssistantWebApplication app,
        string actorId,
        IReadOnlyCollection<string> statuses,
        string? tenantId = null)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var person = new NhAssistantDecidingPerson(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actorId)], "test")),
            actorId,
            tenantId);
        return await scope.ServiceProvider.GetRequiredService<INhAssistantProposalCounts>().CountAsync(person, statuses, CancellationToken.None);
    }

    private static Guid[] Ids(JsonElement inbox)
    {
        return inbox.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("approvalId").GetGuid()).ToArray();
    }

    private static JsonElement Item(JsonElement inbox, Guid approvalId)
    {
        return inbox.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("approvalId").GetGuid() == approvalId);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        Assert.Equal(expected, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    /// <summary>
    /// Projects without a division; the approver approves them and everyone else reads them.
    /// </summary>
    private sealed class StatusProjectResolver : INhAiSubjectAccessResolver
    {
        public string SubjectType => "project";

        public ValueTask<NhAiSubjectInfo?> DescribeAsync(NhAiSubjectRef subject, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<NhAiSubjectInfo?>(new NhAiSubjectInfo(subject, NhAiSubjectInfo.CreateScopeKey(null, null))
            {
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
}
