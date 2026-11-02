using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using Xunit;
using static NewHeap.Platform.AI.Chat.Tests.AssistantInboxOverviewTests;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// Deciding a whole filter of the approval inbox after an overview (FB80): the set the overview shows
/// and its token, a decision of exactly that set with each proposal's own rules, the batch id in the
/// audit, a repeat that reports, a tool that is decided one by one, the bound of a set, and the versions
/// of a decision of several proposals by id; on both relational providers.
/// </summary>
public sealed class AssistantBulkDecisionTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string AddTask = "projects.add-task";
    private const string OtherApprover = "approver-2";

    private static readonly Guid Run = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Start = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_bulk_decision_decides_only_the_set_its_overview_showed_each_with_its_own_rules(AssistantTestProvider provider)
    {
        var clock = new AssistantTestClock(Start);
        var audit = new CapturedBusinessAuditSink();
        await using var app = await StartAsync(provider, clock, audit);
        var now = clock.GetUtcNow();
        var plain = await ProposeAsync(app, TaskRef("n1-a"), Run, now.AddHours(1), now.AddMinutes(-10), AddTask, "Add task");
        var changed = await ProposeAsync(app, TaskRef("n1-b"), Run, now.AddHours(2), now.AddMinutes(-10), AddTask, "Add task");
        var decidedElsewhere = await ProposeAsync(app, TaskRef("n2-a"), Run, now.AddHours(3), now.AddMinutes(-10), AddTask, "Add task");
        // Four-eyes keeps the person who started the work out; another approver rule keeps the caller out.
        var ownWork = await ProposeAsync(
            app, TaskRef("n1-c"), Run, now.AddHours(4), now.AddMinutes(-10), AddTask, "Add task",
            policy: NhAiApprovalPolicy.TriggerDefault with { FourEyes = true },
            initiator: Approver);
        var initiatorOnly = await ProposeAsync(
            app, TaskRef("s1-a"), Run, now.AddHours(5), now.AddMinutes(-10), AddTask, "Add task",
            policy: new NhAiApprovalPolicy("initiator-only") { Mode = NhAiApprovalMode.Deferred, Lifetime = TimeSpan.FromDays(1) },
            initiator: OtherApprover);
        // A message to a supplier is decided one by one, and another approver's proposal is never listed.
        var individual = await ProposeAsync(app, ProjectRef("n1"), Run, now.AddHours(6), now.AddMinutes(-10), IndividualToolCatalog.NotifySupplier);
        var hidden = await ProposeAsync(app, TaskRef("n2-b"), Run, now.AddHours(1), now.AddMinutes(-10), AddTask, member: OtherApprover);
        using var approver = app.CreateClient(Approver);

        var overview = await ReadJsonAsync(await approver.PostAsJsonAsync("/api/assistant/approvals/bulk-decisions/overview", new { }));
        Assert.Equal(
            (5, 5, 6, 0, NhAssistantLimits.MaxBulkDecisionProposals),
            (Int(overview, "count"), Int(overview, "subjects"), Int(overview, "listed"), Int(overview, "remaining"), Int(overview, "limit")));
        Assert.Equal(Start.Add(NhAssistantLimits.BulkDecisionLifetime), overview.GetProperty("expiresAt").GetDateTimeOffset());
        Assert.Equal(
            [(AddTask, "Add task", "project-task", "Task", 5, 5)],
            overview.GetProperty("kinds").EnumerateArray().Select(kind => (
                kind.GetProperty("toolId").GetString(),
                kind.GetProperty("toolDisplayName").GetString(),
                kind.GetProperty("subjectType").GetString(),
                kind.GetProperty("subjectTypeName").GetString(),
                Int(kind, "count"),
                Int(kind, "subjects"))));
        Assert.Equal([(AddTask, 5)], overview.GetProperty("tools").EnumerateArray().Select(tool => (tool.GetProperty("toolId").GetString(), Int(tool, "count"))));
        Assert.Equal([("project-task", 5, 5)], overview.GetProperty("subjectTypes").EnumerateArray().Select(type => (type.GetProperty("subjectType").GetString(), Int(type, "count"), Int(type, "subjects"))));
        var excluded = overview.GetProperty("individual");
        Assert.Equal(1, Int(excluded, "count"));
        Assert.Equal(
            [(IndividualToolCatalog.NotifySupplier, 1)],
            excluded.GetProperty("tools").EnumerateArray().Select(tool => (tool.GetProperty("toolId").GetString(), Int(tool, "count"))));
        var token = overview.GetProperty("token").GetString()!;

        // Between the overview and the decision one proposal changes, a colleague decides another and a
        // new one arrives; none of them is decided with the set.
        await ChangeHashAsync(app, changed);
        await RejectAsync(app, decidedElsewhere);
        var arrived = await ProposeAsync(app, TaskRef("n2-c"), Run, now.AddMinutes(30), now, AddTask, "Add task");

        // The token decides for nobody else, and a token that names no set reads as unknown.
        using var other = app.CreateClient(OtherApprover);
        Assert.Equal(NhAssistantErrorCodes.BulkDecisionNotFound, await RefusedAsync(other, token, HttpStatusCode.NotFound));
        Assert.Equal(NhAssistantErrorCodes.BulkDecisionNotFound, await RefusedAsync(approver, "not-a-token", HttpStatusCode.NotFound));
        using (var missing = await approver.PostAsJsonAsync("/api/assistant/approvals/bulk-decisions", new { decision = "approve" }))
        {
            Assert.Equal(NhAssistantErrorCodes.ApprovalDecisionInvalid, (await ReadJsonAsync(missing, HttpStatusCode.BadRequest)).GetProperty("code").GetString());
        }

        var decided = await ReadJsonAsync(await approver.PostAsJsonAsync(
            "/api/assistant/approvals/bulk-decisions",
            new { token, decision = "approve", reason = "Routine tasks." }));
        Assert.Equal(
            (token, "approve", 1, 2, 2, false),
            (decided.GetProperty("token").GetString(), decided.GetProperty("decision").GetString(), Int(decided, "succeeded"), Int(decided, "refused"), Int(decided, "stale"), decided.GetProperty("repeated").GetBoolean()));
        Assert.Equal(
            [
                (plain, NhAssistantBulkDecisionOutcomes.Succeeded, (string?)null, (string?)NhAssistantApprovalStatuses.Approved),
                (changed, NhAssistantBulkDecisionOutcomes.Stale, NhAssistantErrorCodes.ProposalHashMismatch, null),
                (decidedElsewhere, NhAssistantBulkDecisionOutcomes.Stale, NhAssistantErrorCodes.ApprovalNotPending, null),
                (ownWork, NhAssistantBulkDecisionOutcomes.Refused, NhAssistantErrorCodes.FourEyesRequired, null),
                (initiatorOnly, NhAssistantBulkDecisionOutcomes.Refused, NhAssistantErrorCodes.ApprovalPolicyDenied, null)
            ],
            Items(decided));
        Assert.Equal(NhAssistantApprovalStatuses.Approved, await StatusAsync(app, plain));
        foreach (var untouched in new[] { changed, ownWork, initiatorOnly, individual, hidden, arrived })
        {
            Assert.Equal(NhAssistantApprovalStatuses.Pending, await StatusAsync(app, untouched));
        }

        // Every decision of the set carries its token as the batch id, through the web channel.
        var approved = Assert.Single(audit.Events, evt => evt.Kind == NhAssistantAuditEventKind.ApprovalApproved);
        Assert.Equal((plain, Guid.ParseExact(token, "N"), NhAssistantDecisionChannels.Web), (approved.ApprovalId!.Value, approved.BatchId!.Value, approved.Channel));

        // A repeat reports and decides nothing again, also when it asks for another decision.
        var repeated = await ReadJsonAsync(await approver.PostAsJsonAsync("/api/assistant/approvals/bulk-decisions", new { token, decision = "reject" }));
        Assert.Equal(("approve", 1, 2, 2, true), (repeated.GetProperty("decision").GetString(), Int(repeated, "succeeded"), Int(repeated, "refused"), Int(repeated, "stale"), repeated.GetProperty("repeated").GetBoolean()));
        Assert.Equal(Items(decided), Items(repeated));
        Assert.Single(audit.Events, evt => evt.Kind is NhAssistantAuditEventKind.ApprovalApproved or NhAssistantAuditEventKind.ApprovalRejected);

        // A set not decided within its lifetime is refused; the run maintenance removes sets a day later.
        var late = (await ReadJsonAsync(await approver.PostAsJsonAsync("/api/assistant/approvals/bulk-decisions/overview", new { toolIds = new[] { AddTask } })))
            .GetProperty("token").GetString()!;
        clock.Advance(NhAssistantLimits.BulkDecisionLifetime);
        Assert.Equal(NhAssistantErrorCodes.BulkDecisionExpired, await RefusedAsync(approver, late, HttpStatusCode.Conflict));
        var maintenance = app.Services.GetServices<IHostedService>().OfType<NhAssistantRunMaintenance>().Single();
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var batches = scope.ServiceProvider.GetRequiredService<NhAssistantDecisionBatchStore>();
            Assert.Equal(0, await maintenance.RemoveExpiredDecisionSetsAsync(batches, clock.GetUtcNow(), CancellationToken.None));
            Assert.Equal(2, await maintenance.RemoveExpiredDecisionSetsAsync(batches, clock.GetUtcNow().Add(NhAssistantRunMaintenance.DecisionSetRetention).AddSeconds(1), CancellationToken.None));
        }
        Assert.Equal(NhAssistantErrorCodes.BulkDecisionNotFound, await RefusedAsync(approver, token, HttpStatusCode.NotFound));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_set_another_request_decides_waits_and_one_that_stopped_halfway_is_taken_over(AssistantTestProvider provider)
    {
        var clock = new AssistantTestClock(Start);
        var audit = new CapturedBusinessAuditSink();
        await using var app = await StartAsync(provider, clock, audit);
        var now = clock.GetUtcNow();
        var first = await ProposeAsync(app, TaskRef("n1-a"), Run, now.AddHours(1), now.AddMinutes(-10), AddTask);
        var second = await ProposeAsync(app, TaskRef("n1-b"), Run, now.AddHours(2), now.AddMinutes(-10), AddTask);
        using var approver = app.CreateClient(Approver);
        var token = (await ReadJsonAsync(await approver.PostAsJsonAsync("/api/assistant/approvals/bulk-decisions/overview", new { agentId = ProjectAgent })))
            .GetProperty("token").GetString()!;
        var batchId = Guid.ParseExact(token, "N");

        // Another request claimed the set and rejected the first proposal before it stopped.
        await using (var scope = app.Services.CreateAsyncScope())
        {
            Assert.True(await scope.ServiceProvider.GetRequiredService<NhAssistantDecisionBatchStore>()
                .TryClaimAsync(batchId, "reject", clock.GetUtcNow(), clock.GetUtcNow().AddMinutes(-5), CancellationToken.None));
        }
        await RejectAsync(app, first, Approver);
        Assert.Equal(NhAssistantErrorCodes.BulkDecisionInProgress, await RefusedAsync(approver, token, HttpStatusCode.Conflict));

        // After the lease a repeat takes it over with the decision it began with, and counts the
        // proposal it already decided.
        clock.Advance(NhAssistantBulkDecisions.ClaimLease + TimeSpan.FromSeconds(1));
        var decided = await ReadJsonAsync(await approver.PostAsJsonAsync("/api/assistant/approvals/bulk-decisions", new { token, decision = "approve" }));
        Assert.Equal(("reject", 2, 0, 0, false), (decided.GetProperty("decision").GetString(), Int(decided, "succeeded"), Int(decided, "refused"), Int(decided, "stale"), decided.GetProperty("repeated").GetBoolean()));
        Assert.Equal(NhAssistantApprovalStatuses.Rejected, await StatusAsync(app, second));
        var rejected = Assert.Single(audit.Events, evt => evt.Kind == NhAssistantAuditEventKind.ApprovalRejected);
        Assert.Equal((second, batchId), (rejected.ApprovalId!.Value, rejected.BatchId!.Value));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_set_has_an_upper_bound_and_the_overview_says_what_remains(AssistantTestProvider provider)
    {
        var clock = new AssistantTestClock(Start);
        await using var app = await StartAsync(provider, clock, new CapturedBusinessAuditSink());
        var now = clock.GetUtcNow();
        var first = await ProposeAsync(app, TaskRef("n1-a"), Run, now.AddHours(1), now.AddMinutes(-10), AddTask);
        await CopyAsync(app, first, NhAssistantLimits.MaxBulkDecisionProposals, now.AddHours(2));
        using var approver = app.CreateClient(Approver);

        // The first proposals in the inbox's order fill the set; the rest waits for a next round.
        var overview = await ReadJsonAsync(await approver.PostAsJsonAsync("/api/assistant/approvals/bulk-decisions/overview", new { }));
        Assert.Equal(
            (NhAssistantLimits.MaxBulkDecisionProposals, NhAssistantLimits.MaxBulkDecisionProposals + 1, 1, 1),
            (Int(overview, "count"), Int(overview, "listed"), Int(overview, "remaining"), Int(overview, "subjects")));
        var empty = await ReadJsonAsync(await approver.PostAsJsonAsync("/api/assistant/approvals/bulk-decisions/overview", new { toolIds = new[] { "projects.none" } }));
        Assert.Equal((0, 0, JsonValueKind.Null), (Int(empty, "count"), Int(empty, "listed"), empty.GetProperty("token").ValueKind));
        using var outsider = app.CreateClient(Outsider);
        var nothing = await ReadJsonAsync(await outsider.PostAsJsonAsync("/api/assistant/approvals/bulk-decisions/overview", new { }));
        Assert.Equal((0, 0, JsonValueKind.Null), (Int(nothing, "count"), Int(nothing, "listed"), nothing.GetProperty("token").ValueKind));
        using (var invalid = await approver.PostAsJsonAsync("/api/assistant/approvals/bulk-decisions/overview", new { sort = "title" }))
        {
            Assert.Equal(NhAssistantErrorCodes.MessageInvalid, (await ReadJsonAsync(invalid, HttpStatusCode.BadRequest)).GetProperty("code").GetString());
        }
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Deciding_several_by_id_checks_the_versions_sent_and_leaves_individual_decisions(AssistantTestProvider provider)
    {
        var clock = new AssistantTestClock(Start);
        var audit = new CapturedBusinessAuditSink();
        await using var app = await StartAsync(provider, clock, audit);
        var now = clock.GetUtcNow();
        var seen = await ProposeAsync(app, TaskRef("n1-a"), Run, now.AddHours(1), now.AddMinutes(-10), AddTask);
        var changed = await ProposeAsync(app, TaskRef("n1-b"), Run, now.AddHours(1), now.AddMinutes(-10), AddTask);
        var unversioned = await ProposeAsync(app, TaskRef("n1-c"), Run, now.AddHours(1), now.AddMinutes(-10), AddTask);
        var individual = await ProposeAsync(app, ProjectRef("n1"), Run, now.AddHours(1), now.AddMinutes(-10), IndividualToolCatalog.NotifySupplier);
        var single = await ProposeAsync(app, TaskRef("n2-a"), Run, now.AddHours(1), now.AddMinutes(-10), AddTask);
        var hashes = await HashesAsync(app, seen, changed, individual, single);
        await ChangeHashAsync(app, changed);
        using var approver = app.CreateClient(Approver);

        var response = await ReadJsonAsync(await approver.PostAsJsonAsync(
            "/api/assistant/approvals/decisions",
            new
            {
                approvalIds = new[] { seen, changed, unversioned, individual },
                decision = "reject",
                expectedProposalHashes = new Dictionary<Guid, string>
                {
                    [seen] = hashes[seen],
                    [changed] = hashes[changed],
                    [individual] = hashes[individual]
                }
            }));
        Assert.Equal(
            [
                (seen, true, (string?)null),
                (changed, false, (string?)NhAssistantErrorCodes.ProposalHashMismatch),
                (unversioned, true, null),
                (individual, false, NhAssistantErrorCodes.IndividualDecisionRequired)
            ],
            response.GetProperty("results").EnumerateArray().Select(result => (
                result.GetProperty("approvalId").GetGuid(),
                result.GetProperty("succeeded").GetBoolean(),
                result.GetProperty("code").GetString())));
        Assert.Equal(NhAssistantApprovalStatuses.Pending, await StatusAsync(app, individual));

        // One proposal of such a tool is decided on its own, and that decision carries no batch id; the
        // decisions of one request share one.
        using (var own = await approver.PostAsJsonAsync($"/api/assistant/approvals/{individual}/decisions", new { decision = "reject", expectedProposalHash = hashes[individual] }))
        {
            Assert.Equal(NhAssistantApprovalStatuses.Rejected, (await ReadJsonAsync(own)).GetProperty("status").GetString());
        }
        using (var one = await approver.PostAsJsonAsync($"/api/assistant/approvals/{single}/decisions", new { decision = "reject", expectedProposalHash = hashes[single] }))
        {
            Assert.Equal(HttpStatusCode.OK, one.StatusCode);
        }
        var events = audit.Events.Where(evt => evt.Kind == NhAssistantAuditEventKind.ApprovalRejected).ToDictionary(evt => evt.ApprovalId!.Value);
        Assert.NotNull(events[seen].BatchId);
        Assert.Equal(events[seen].BatchId, events[unversioned].BatchId);
        Assert.Null(events[individual].BatchId);
        Assert.Null(events[single].BatchId);
    }

    private Task<AssistantWebApplication> StartAsync(AssistantTestProvider provider, AssistantTestClock clock, CapturedBusinessAuditSink audit)
    {
        return StartInboxAsync(database, provider, services =>
        {
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton(audit);
            services.AddScoped<INhAssistantBusinessAuditSink, CapturedBusinessAuditSinkAdapter>();
            services.AddNewHeapPlatformAI(ai => ai.AddGeneratedToolCatalog<IndividualToolCatalog>());
            // Approved proposals wait for agent work, which these seeded proposals have no execution for.
            services.PostConfigure<NhAssistantOptions>(options => options.AgentWork = false);
        });
    }

    private static async Task<string?> RefusedAsync(HttpClient client, string token, HttpStatusCode status)
    {
        using var response = await client.PostAsJsonAsync("/api/assistant/approvals/bulk-decisions", new { token, decision = "approve" });
        return (await ReadJsonAsync(response, status)).GetProperty("code").GetString();
    }

    private static (Guid, string?, string?, string?)[] Items(JsonElement result)
    {
        return result.GetProperty("items").EnumerateArray()
            .Select(item => (
                item.GetProperty("approvalId").GetGuid(),
                item.GetProperty("outcome").GetString(),
                item.GetProperty("code").GetString(),
                item.GetProperty("status").GetString()))
            .ToArray();
    }

    private static int Int(JsonElement element, string property)
    {
        return element.GetProperty(property).GetInt32();
    }

    private static async Task ChangeHashAsync(AssistantWebApplication app, Guid approvalId)
    {
        await using var context = app.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        await context.Approvals
            .Where(approval => approval.Id == approvalId)
            .ExecuteUpdateAsync(update => update.SetProperty(approval => approval.ProposalHash, "sha256:changed-" + approvalId.ToString("N")));
    }

    private static async Task<Dictionary<Guid, string>> HashesAsync(AssistantWebApplication app, params Guid[] approvalIds)
    {
        await using var context = app.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        return await context.Approvals
            .Where(approval => approvalIds.Contains(approval.Id))
            .ToDictionaryAsync(approval => approval.Id, approval => approval.ProposalHash);
    }

    private static async Task RejectAsync(AssistantWebApplication app, Guid approvalId, string decidedBy = OtherApprover)
    {
        await using var scope = app.Services.CreateAsyncScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<NhAssistantRunStore>().TryTransitionApprovalAsync(
            approvalId,
            [NhAssistantApprovalStatuses.Pending],
            NhAssistantApprovalStatuses.Rejected,
            new NhAssistantApprovalTransition { DecidedByActorId = decidedBy, DecidedAt = DateTimeOffset.UtcNow },
            CancellationToken.None));
    }

    private static async Task<string> StatusAsync(AssistantWebApplication app, Guid approvalId)
    {
        await using var context = app.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        return await context.Approvals.Where(approval => approval.Id == approvalId).Select(approval => approval.Status).SingleAsync();
    }

    /// <summary>
    /// Adds <paramref name="count"/> copies of a pending proposal in its thread, each expiring at
    /// <paramref name="expiresAt"/>, in one save.
    /// </summary>
    private static async Task CopyAsync(AssistantWebApplication app, Guid approvalId, int count, DateTimeOffset expiresAt)
    {
        await using var context = app.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        var original = await context.Approvals.AsNoTracking().SingleAsync(approval => approval.Id == approvalId);
        for (var index = 0; index < count; index++)
        {
            context.Approvals.Add(new Entities.AssistantApproval
            {
                Id = Guid.NewGuid(),
                ConversationId = original.ConversationId,
                TurnId = Guid.NewGuid(),
                ToolInvocationId = Guid.NewGuid(),
                ProposalId = Guid.NewGuid(),
                ProposalHash = "sha256:" + Guid.NewGuid().ToString("N"),
                ProposalJson = original.ProposalJson,
                ToolId = original.ToolId,
                Summary = original.Summary,
                ArgumentsPreview = original.ArgumentsPreview,
                Status = NhAssistantApprovalStatuses.Pending,
                CreatedAt = original.CreatedAt,
                ExpiresAt = expiresAt,
                RunId = original.RunId,
                Mode = original.Mode,
                ParentSubjectType = original.ParentSubjectType,
                ParentSubjectId = original.ParentSubjectId,
                ParentDisplayName = original.ParentDisplayName,
                ConcurrencyStamp = Guid.NewGuid()
            });
        }
        await context.SaveChangesAsync();
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        Assert.Equal(expected, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    /// <summary>
    /// One tool whose proposals are decided one by one, as a message to an external party is.
    /// </summary>
    private sealed class IndividualToolCatalog : INhAiGeneratedToolCatalog
    {
        public const string NotifySupplier = "projects.notify-supplier";

        private static readonly NhAiToolDescriptor Notify = new(
            NotifySupplier,
            1,
            "Tell the supplier about the project.",
            typeof(JsonElement),
            typeof(JsonElement),
            NhAiToolEffect.ExternalSideEffect,
            NhAiToolExposure.Agent,
            true,
            [TestProjectToolCatalog.ReadPolicy])
        {
            ExportName = "projects_notify_supplier_v1",
            CatalogId = "project-supplier",
            ContractHash = new string('9', 64),
            SchemaHash = new string('9', 64),
            Approval = NhAiApprovalRequirement.Required,
            Idempotency = NhAiIdempotencySupport.Required,
            RequireIndividualDecision = true
        };

        public NhAiToolCatalogGovernance Governance => NhAiToolCatalogGovernance.SharedInvoker;

        public IReadOnlyList<NhAiToolDescriptor> Descriptors { get; } = [Notify];

        public NhAiToolCatalogManifest Manifest { get; } = new(
            "project-supplier",
            1,
            new string('9', 64),
            [new NhAiToolManifestEntry(NotifySupplier, 1, new string('9', 64), new string('9', 64)) { ExportName = "projects_notify_supplier_v1" }]);

        public IReadOnlyList<AIFunction> CreateFunctions(IServiceProvider services)
        {
            return [];
        }
    }
}
