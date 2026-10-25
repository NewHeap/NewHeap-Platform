using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.AspNet.Common;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.PostgreSql;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NewHeap.Platform.AspNet.Common.SqlServer;
using NewHeap.Platform.Tests.Relational;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// Finding the run a trigger event started (LS13), on both relational providers: the receipts name
/// the queued input that holds the event, also for a deferred fire before the commit, and
/// <see cref="INhAssistantAgentRunLookup"/> finds the input by its event key or its id with the run that
/// consumed it, or while it waits the run the event started. An event that coalesced into another
/// event's run finds that run through its input, a redelivery finds the first delivery, and a lookup
/// for another tenant finds nothing. The agent's projects belong to one tenant, which takes part in the
/// whole application's work by the single-tenant mode.
/// </summary>
public sealed class AssistantTriggerEventLookupTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string AgentId = "project-assistant";
    private const string OrderTrigger = "order-changed";
    private const string ReportTrigger = "application-report";
    private const string Tenant = "tenant-a";
    private const string OtherTenant = "tenant-b";

    private static readonly RelationalTestPool SqlServerApplications = CreateApplicationPool(AssistantTestProvider.SqlServer);
    private static readonly RelationalTestPool PostgreSqlApplications = CreateApplicationPool(AssistantTestProvider.PostgreSql);

    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_started_coalesced_and_redelivered_event_each_find_the_run_that_took_them(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        var projectId = Guid.NewGuid();

        // The first event starts a run; its input waits until the run starts and claims it.
        var started = Assert.Single(await PublishAsync(host, new OrderChanged(projectId, "order-1:v1", "created")));
        Assert.Equal(NhAssistantTriggerOutcomes.Started, started.Outcome);
        Assert.NotNull(started.InputId);
        var beforeStart = (await FindByEventAsync(host, "order-1:v1"))!;
        Assert.Equal((started.InputId, started.RunId, true, NhAssistantRunStatuses.Queued), (beforeStart.InputId, beforeStart.RunId, beforeStart.Waiting, beforeStart.RunStatus));
        Assert.Equal((AgentId, OrderTrigger, "order-1:v1", started.ConversationId!.Value, false), (beforeStart.AgentId, beforeStart.TriggerId, beforeStart.EventKey, beforeStart.ConversationId, beforeStart.Shadow));
        Assert.Equal(new NhAiSubjectRef("project", projectId.ToString("N")), beforeStart.Subject);

        // The second joins the queued run. The run records the first event's key only, so the second
        // is found through its own input: it waits without a run of its own until the run claims it.
        var coalesced = Assert.Single(await PublishAsync(host, new OrderChanged(projectId, "order-2:v1", "quantity 6")));
        Assert.Equal((NhAssistantTriggerOutcomes.Coalesced, started.RunId), (coalesced.Outcome, coalesced.RunId));
        Assert.NotNull(coalesced.InputId);
        Assert.NotEqual(started.InputId, coalesced.InputId);
        var joined = (await FindByEventAsync(host, "order-2:v1"))!;
        Assert.Equal((coalesced.InputId!.Value, true, (Guid?)null), (joined.InputId, joined.Waiting, joined.RunId));

        // The run starts and claims both inputs; both events now name it.
        await host.Services.GetRequiredService<NhAssistantRunStore>().ClaimQueuedInputsAsync(
            started.ConversationId.Value,
            started.RunId!.Value,
            CancellationToken.None);
        foreach (var (eventKey, inputId) in new[] { ("order-1:v1", started.InputId), ("order-2:v1", coalesced.InputId) })
        {
            var claimed = (await FindByEventAsync(host, eventKey))!;
            Assert.Equal((inputId!.Value, false, started.RunId), (claimed.InputId, claimed.Waiting, claimed.RunId));
            Assert.NotNull(claimed.ConsumedAt);
            Assert.Equal(claimed, await FindByInputAsync(host, inputId.Value));
        }

        // A redelivery writes no input; the lookup still finds the first delivery and its run.
        var redelivered = Assert.Single(await PublishAsync(host, new OrderChanged(projectId, "order-1:v1", "created")));
        Assert.Equal((NhAssistantTriggerOutcomes.Deduplicated, (Guid?)null), (redelivered.Outcome, redelivered.InputId));
        var original = (await FindByEventAsync(host, "order-1:v1"))!;
        Assert.Equal((started.InputId!.Value, started.RunId), (original.InputId, original.RunId));

        // An unknown key, another trigger and another agent find nothing.
        Assert.Null(await FindByEventAsync(host, "order-9:v1"));
        Assert.Null(await LookupAsync(host, lookup => lookup.FindByEventAsync(AgentId, ReportTrigger, "order-1:v1")));
        Assert.Null(await LookupAsync(host, lookup => lookup.FindByEventAsync("other-agent", OrderTrigger, "order-1:v1")));
        Assert.Null(await FindByInputAsync(host, Guid.NewGuid()));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_deferred_fire_names_its_input_before_the_commit_and_finds_its_run_after_it(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        var projectId = Guid.NewGuid();

        // Inside the transaction nothing is written yet, but the receipt already names the input.
        var deferred = Assert.Single(await PublishInTransactionAsync(host, new OrderChanged(projectId, "order-1:v1", "created"), commit: true));
        Assert.Equal((NhAssistantTriggerOutcomes.Deferred, (Guid?)null), (deferred.Outcome, deferred.RunId));
        Assert.NotNull(deferred.InputId);
        Assert.Null(await FindByEventAsync(host, "order-1:v1"));
        Assert.Null(await FindByInputAsync(host, deferred.InputId!.Value));

        // After the commit the fire runs and writes that input; both lookups find it and its run.
        var request = Assert.Single(await DeferredAsync(host, projectId));
        Assert.Equal(deferred.InputId, request.InputId);
        var fired = await FireDeferredAsync(host, request);
        Assert.Equal((NhAssistantTriggerOutcomes.Started, deferred.InputId), (fired.Outcome, fired.InputId));
        var byEvent = (await FindByEventAsync(host, "order-1:v1"))!;
        Assert.Equal((deferred.InputId!.Value, fired.RunId, true), (byEvent.InputId, byEvent.RunId, byEvent.Waiting));
        Assert.Equal(byEvent, await FindByInputAsync(host, deferred.InputId.Value));

        // A retried fire finds its input and names it again.
        var retried = await FireDeferredAsync(host, request);
        Assert.Equal((NhAssistantTriggerOutcomes.Deduplicated, deferred.InputId), (retried.Outcome, retried.InputId));

        // A rolled-back publication names an input that is never written.
        var otherProject = Guid.NewGuid();
        var rolledBack = Assert.Single(await PublishInTransactionAsync(host, new OrderChanged(otherProject, "order-2:v1", "created"), commit: false));
        Assert.NotNull(rolledBack.InputId);
        Assert.Empty(await DeferredAsync(host, otherProject));
        Assert.Null(await FindByInputAsync(host, rolledBack.InputId!.Value));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task An_event_that_waits_for_a_paused_agent_is_found_waiting_and_then_with_the_run_of_the_resume(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        var projectId = Guid.NewGuid();
        await ControlAsync(host, control => control.PauseAsync(AgentId, "operator-1"));

        var waiting = Assert.Single(await PublishAsync(host, new OrderChanged(projectId, "order-1:v1", "created")));
        Assert.Equal(NhAssistantTriggerOutcomes.Waiting, waiting.Outcome);
        Assert.NotNull(waiting.InputId);
        var paused = (await FindByEventAsync(host, "order-1:v1"))!;
        Assert.Equal((waiting.InputId!.Value, true, (Guid?)null, (string?)null), (paused.InputId, paused.Waiting, paused.RunId, paused.RunStatus));

        // The resume starts a run for the thread that records the waiting event and claims its input.
        var resumed = await ControlAsync(host, control => control.ResumeAsync(AgentId, "operator-1"));
        Assert.Equal(1, resumed.Data!.StartedRuns);
        var queued = (await FindByEventAsync(host, "order-1:v1"))!;
        Assert.True(queued.Waiting);
        Assert.NotNull(queued.RunId);
        Assert.Equal(NhAssistantRunStatuses.Queued, queued.RunStatus);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_lookup_for_another_tenant_finds_nothing(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        var project = Assert.Single(await PublishAsync(host, new OrderChanged(Guid.NewGuid(), "order-1:v1", "created")));
        var report = Assert.Single(await PublishAsync(host, new ApplicationReport("report-2026-10")));
        Assert.Equal((NhAssistantTriggerOutcomes.Started, NhAssistantTriggerOutcomes.Started), (project.Outcome, report.Outcome));

        // The project's thread belongs to the tenant: its own tenant and the application find it.
        Assert.Equal(project.InputId, (await FindByEventAsync(host, "order-1:v1", Tenant))?.InputId);
        Assert.Equal(project.InputId, (await FindByEventAsync(host, "order-1:v1"))?.InputId);
        Assert.Equal(project.InputId, (await FindByInputAsync(host, project.InputId!.Value, Tenant))?.InputId);
        Assert.Null(await FindByEventAsync(host, "order-1:v1", OtherTenant));
        Assert.Null(await FindByInputAsync(host, project.InputId.Value, OtherTenant));

        // The whole application's thread is stored without a tenant; the single tenant takes part.
        var whole = (await LookupAsync(host, lookup => lookup.FindByEventAsync(AgentId, ReportTrigger, "report-2026-10", Tenant)))!;
        Assert.Equal((report.InputId!.Value, NhAiSystemSubjects.Global), (whole.InputId, whole.Subject));
        Assert.Null(await LookupAsync(host, lookup => lookup.FindByEventAsync(AgentId, ReportTrigger, "report-2026-10", OtherTenant)));
        Assert.Null(await FindByInputAsync(host, report.InputId.Value, OtherTenant));
    }

    private async Task<AssistantTestHost> CreateHostAsync(AssistantTestProvider provider)
    {
        // The application's own database holds its orders and the background operations, so a trigger
        // published in its transaction is deferred until the commit.
        var pool = provider == AssistantTestProvider.SqlServer ? SqlServerApplications : PostgreSqlApplications;
        var application = await pool.LeaseAsync();
        try
        {
            var host = await AssistantTestHost.CreateAsync(
                database,
                provider,
                new NhAiScriptedChatClient(),
                configure: services =>
                {
                    services.AddSingleton(_ => application);
                    services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, TestPrincipalFactory>());
                    services.AddDbContext<OrderDbContext>(options => UseApplicationDatabase(options, provider, application.ConnectionString));
                    services.AddSingleton<INhBackgroundOperationScheduler, NoOpScheduler>();
                    services.AddNewHeapBackgroundOperations<OrderDbContext>(operations =>
                    {
                        operations.Options.DispatchWorkersEnabled = false;
                        operations.Options.LiveUpdatesEnabled = false;
                        operations.Options.ReconciliationEnabled = false;
                        operations.Options.CleanupEnabled = false;
                        operations.Options.UserNotificationProjectionEnabled = false;
                        operations.AddAssistantAgentRuns();
                    });
                },
                assistantBuilder: assistant => assistant
                    .AddSubjectType<TenantProjectResolver>()
                    .UseSingleTenant(Tenant)
                    .ConfigureAgent(AgentId, agent => agent
                        .Grant("projects.*")
                        .WorksOn("project")
                        .WorksOnApplication())
                    .AddTrigger<OrderChanged>(OrderTrigger, trigger => trigger
                        .StartAgent(AgentId)
                        .Subject(evt => new NhAiSubjectRef("project", evt.ProjectId.ToString("N")))
                        .Message(evt => "Order " + evt.Change)
                        .Dedupe(evt => evt.Key)
                        .OnBusy(NhAssistantTriggerConflict.Coalesce, TimeSpan.Zero))
                    .AddTrigger<ApplicationReport>(ReportTrigger, trigger => trigger
                        .StartAgent(AgentId)
                        .Subject(_ => NhAiSystemSubjects.Global)
                        .Message(evt => "Report " + evt.Key)
                        .Dedupe(evt => evt.Key)));

            // The host's services own the lease from here on.
            host.Services.GetRequiredService<RelationalTestLease>();
            return host;
        }
        catch
        {
            await application.DisposeAsync();
            throw;
        }
    }

    private static RelationalTestPool CreateApplicationPool(AssistantTestProvider provider)
    {
        return new RelationalTestPool(
            AssistantDatabaseFixture.ToRelational(provider),
            "lookups",
            RelationalTestPoolLayout.DatabasePerSlot,
            async slot =>
            {
                var options = new DbContextOptionsBuilder<OrderDbContext>();
                UseApplicationDatabase(options, provider, slot.ConnectionString);
                await using var context = new OrderDbContext(options.Options);
                await context.Database.EnsureCreatedAsync();
            });
    }

    private static void UseApplicationDatabase(DbContextOptionsBuilder options, AssistantTestProvider provider, string connectionString)
    {
        if (provider == AssistantTestProvider.SqlServer)
        {
            options.UseNewHeapSqlServer(connectionString);
        }
        else
        {
            options.UseNewHeapPostgreSql(connectionString);
        }
    }

    private static async Task<IReadOnlyList<NhAssistantTriggerReceipt>> PublishAsync<TEvent>(AssistantTestHost host, TEvent evt)
        where TEvent : class
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INhAssistantAgentTriggers>().PublishAsync(evt);
    }

    private static async Task<IReadOnlyList<NhAssistantTriggerReceipt>> PublishInTransactionAsync(AssistantTestHost host, OrderChanged evt, bool commit)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        await using var transaction = await context.Database.BeginTransactionAsync();
        context.Orders.Add(new OrderRow { Id = Guid.NewGuid(), ProjectId = evt.ProjectId, Change = evt.Change });
        await context.SaveChangesAsync();
        var receipts = await scope.ServiceProvider.GetRequiredService<INhAssistantAgentTriggers>().PublishAsync(evt);
        if (commit)
        {
            await transaction.CommitAsync();
        }
        else
        {
            await transaction.RollbackAsync();
        }

        return receipts;
    }

    /// <summary>
    /// The deferred fires of a project that the application committed, oldest first.
    /// </summary>
    private static async Task<IReadOnlyList<NhAssistantFireTriggerRequest>> DeferredAsync(AssistantTestHost host, Guid projectId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var subjectId = projectId.ToString("N");
        var payloads = await scope.ServiceProvider.GetRequiredService<OrderDbContext>().Set<NhBackgroundOperation>()
            .AsNoTracking()
            .Where(operation => operation.OperationType == NhAssistantAgentRunOperationTypes.FireTrigger
                && operation.DomainObjectId == subjectId)
            .OrderBy(operation => operation.CreationDateTime)
            .Select(operation => operation.PayloadJson)
            .ToListAsync();
        return payloads
            .Select(payload => JsonSerializer.Deserialize<NhAssistantFireTriggerRequest>(payload, PayloadJson)!)
            .ToArray();
    }

    /// <summary>
    /// Runs a deferred fire as its background operation does once the transaction committed.
    /// </summary>
    private static async Task<NhAssistantTriggerReceipt> FireDeferredAsync(AssistantTestHost host, NhAssistantFireTriggerRequest request)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NhAssistantAgentTriggers>()
            .FireDeferredAsync(request, CancellationToken.None);
    }

    private static Task<NhAssistantTriggerEventRun?> FindByEventAsync(AssistantTestHost host, string eventKey, string? tenantId = null)
    {
        return LookupAsync(host, lookup => lookup.FindByEventAsync(AgentId, OrderTrigger, eventKey, tenantId));
    }

    private static Task<NhAssistantTriggerEventRun?> FindByInputAsync(AssistantTestHost host, Guid inputId, string? tenantId = null)
    {
        return LookupAsync(host, lookup => lookup.FindByInputAsync(inputId, tenantId));
    }

    private static async Task<NhAssistantTriggerEventRun?> LookupAsync(
        AssistantTestHost host,
        Func<INhAssistantAgentRunLookup, Task<NhAssistantTriggerEventRun?>> find)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await find(scope.ServiceProvider.GetRequiredService<INhAssistantAgentRunLookup>());
    }

    private static async Task<T> ControlAsync<T>(AssistantTestHost host, Func<INhAssistantAgentControl, Task<T>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<INhAssistantAgentControl>());
    }

    private sealed class NoOpScheduler : INhBackgroundOperationScheduler
    {
        public Task<NhBackgroundOperationScheduleResult> EnqueueAsync(
            Guid operationId,
            int dispatchGeneration,
            string queue,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new NhBackgroundOperationScheduleResult("noop"));
        }

        public Task<bool> DeleteAsync(string schedulerJobId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        public Task<NhBackgroundOperationExecutionState?> GetStateAsync(string schedulerJobId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<NhBackgroundOperationExecutionState?>(null);
        }
    }
}

/// <param name="Key">The dedupe key of the report, such as its month.</param>
public sealed record ApplicationReport(string Key);

/// <summary>
/// Projects of one tenant, whose members are those of every test project.
/// </summary>
public sealed class TenantProjectResolver : INhAiSubjectAccessResolver
{
    public const string TenantId = "tenant-a";

    private static readonly TestSubjects Members = new();

    public string SubjectType => "project";

    public ValueTask<NhAiSubjectInfo?> DescribeAsync(NhAiSubjectRef subject, CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult<NhAiSubjectInfo?>(new NhAiSubjectInfo(subject, NhAiSubjectInfo.CreateScopeKey(TenantId, null))
        {
            TenantId = TenantId,
            Version = "1",
            DisplayName = "Project " + subject.Id[..8]
        });
    }

    public ValueTask<NhAiSubjectAccess> ResolveAsync(NhAiSubjectRef subject, string actorId, CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(Members.Roles.TryGetValue(actorId, out var role)
            ? new NhAiSubjectAccess(role)
            : NhAiSubjectAccess.None);
    }

    public async IAsyncEnumerable<NhAiSubjectMember> ListMembersAsync(
        NhAiSubjectRef subject,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var (actorId, role) in Members.Roles)
        {
            await Task.Yield();
            yield return new NhAiSubjectMember(actorId, role) { DisplayName = actorId };
        }
    }
}
