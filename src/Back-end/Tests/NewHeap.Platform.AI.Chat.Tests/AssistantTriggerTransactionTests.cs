using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.AspNet.Common;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.PostgreSql;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NewHeap.Platform.AspNet.Common.SqlServer;
using Npgsql;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// Agent triggers published inside the application's transaction, with the background operations
/// in the application's own context and database: a rollback leaves nothing behind, a committed
/// publication fires after the commit, and publishing after the commit is unchanged, on both
/// relational providers.
/// </summary>
public sealed class AssistantTriggerTransactionTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string AgentId = "project-assistant";
    private const string TriggerId = "order-changed";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_rolled_back_publication_leaves_nothing_and_a_retried_commit_fires_after_it(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        var projectId = Guid.NewGuid();

        // Published inside the transaction, then rolled back: no thread, run, input or operation.
        var rolledBack = Assert.Single(await PublishAsync(host, new OrderChanged(projectId, "order-1:v1", "quantity 5"), commit: false));
        Assert.Equal(NhAssistantTriggerOutcomes.Deferred, rolledBack.Outcome);
        Assert.Null(rolledBack.RunId);
        Assert.Null(rolledBack.ConversationId);
        Assert.Empty(await ThreadsAsync(host, projectId));
        Assert.Empty(await DeferredAsync(host, projectId));

        // The application retries the same change with the same dedupe key and commits. Nothing
        // reaches the assistant before the commit; afterwards one run receives only this input.
        var committed = Assert.Single(await PublishAsync(host, new OrderChanged(projectId, "order-1:v1", "quantity 6"), commit: true));
        Assert.Equal(NhAssistantTriggerOutcomes.Deferred, committed.Outcome);
        Assert.Empty(await ThreadsAsync(host, projectId));
        var fired = Assert.Single(await DeliverAsync(host, projectId));
        Assert.Equal(NhAssistantTriggerOutcomes.Started, fired.Outcome);

        var thread = Assert.Single(await ThreadsAsync(host, projectId));
        var run = Assert.Single(await RunsAsync(host, thread.ConversationId));
        Assert.Equal(fired.RunId, run.Id);
        Assert.Equal(NhAssistantRunStatuses.Queued, run.Status);
        Assert.Equal("order-1:v1", run.TriggerEventKey);
        Assert.True(await OperationExistsAsync(host, run.BackgroundOperationId));
        var input = Assert.Single(await InputsAsync(host, thread.ConversationId));
        Assert.Equal("Order quantity 6", input.Text);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Publications_of_one_transaction_coalesce_after_the_commit_and_a_duplicate_starts_nothing(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        var projectId = Guid.NewGuid();

        var receipts = await PublishAsync(
            host,
            [new OrderChanged(projectId, "order-1:v1", "created"), new OrderChanged(projectId, "order-2:v1", "created")],
            commit: true);
        Assert.All(receipts, receipt => Assert.Equal(NhAssistantTriggerOutcomes.Deferred, receipt.Outcome));

        // In publishing order: the first starts the run, the second joins it before it runs.
        var fired = await DeliverAsync(host, projectId);
        Assert.Equal([NhAssistantTriggerOutcomes.Started, NhAssistantTriggerOutcomes.Coalesced], fired.Select(receipt => receipt.Outcome));
        var thread = Assert.Single(await ThreadsAsync(host, projectId));
        var run = Assert.Single(await RunsAsync(host, thread.ConversationId));
        Assert.Equal(2, (await InputsAsync(host, thread.ConversationId)).Count);

        // The same event again, committed: deduplicated, no second run.
        await PublishAsync(host, new OrderChanged(projectId, "order-1:v1", "created"), commit: true);
        var duplicate = Assert.Single(await DeliverAsync(host, projectId));
        Assert.Equal(NhAssistantTriggerOutcomes.Deduplicated, duplicate.Outcome);
        Assert.Equal(run.Id, Assert.Single(await RunsAsync(host, thread.ConversationId)).Id);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Publishing_after_the_commit_fires_at_once_and_a_retried_operation_fires_once(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        var projectId = Guid.NewGuid();

        // Without a transaction nothing changes: the receipt names the run and its thread.
        var receipt = Assert.Single(await PublishWithoutTransactionAsync(host, new OrderChanged(projectId, "order-1:v1", "created")));
        Assert.Equal(NhAssistantTriggerOutcomes.Started, receipt.Outcome);
        Assert.NotNull(receipt.RunId);
        Assert.NotNull(receipt.ConversationId);
        Assert.Empty(await DeferredAsync(host, projectId));

        // A deferred operation that runs again after it fired writes its input only once.
        var otherProject = Guid.NewGuid();
        await PublishAsync(host, new OrderChanged(otherProject, null, "created"), commit: true);
        var request = Assert.Single(await DeferredAsync(host, otherProject));
        Assert.Equal(NhAssistantTriggerOutcomes.Started, (await FireAsync(host, request)).Outcome);
        Assert.Equal(NhAssistantTriggerOutcomes.Deduplicated, (await FireAsync(host, request)).Outcome);
        var thread = Assert.Single(await ThreadsAsync(host, otherProject));
        Assert.Single(await RunsAsync(host, thread.ConversationId));
        Assert.Equal(request.InputId, Assert.Single(await InputsAsync(host, thread.ConversationId)).Id);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Maintenance_queues_a_run_again_that_lost_its_operation(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        var projectId = Guid.NewGuid();
        var receipt = Assert.Single(await PublishWithoutTransactionAsync(host, new OrderChanged(projectId, "order-1:v1", "created")));
        var conversationId = receipt.ConversationId!.Value;

        // The process stopped between writing a run and enqueuing its operation, long ago.
        var orphan = new AssistantRun
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            TriggerId = TriggerId,
            TriggerEventKey = "order-2:v1",
            AccountableOwnerId = (await RunsAsync(host, conversationId))[0].AccountableOwnerId,
            Status = NhAssistantRunStatuses.Queued,
            AgentVersion = 1,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-15),
            ConcurrencyStamp = Guid.NewGuid()
        };
        await host.Services.GetRequiredService<NhAssistantRunStore>().AddRunAsync(orphan, CancellationToken.None);

        await host.Services.GetServices<IHostedService>().OfType<NhAssistantRunMaintenance>().Single()
            .RunOnceAsync(CancellationToken.None);

        var recovered = Assert.Single(await RunsAsync(host, conversationId), run => run.Id == orphan.Id);
        Assert.Equal(NhAssistantRunStatuses.Queued, recovered.Status);
        Assert.True(await OperationExistsAsync(host, recovered.BackgroundOperationId));
    }

    private async Task<AssistantTestHost> CreateHostAsync(AssistantTestProvider provider)
    {
        // The application's database differs from the assistant's: deferring needs neither a shared
        // database nor a shared connection.
        var application = await ApplicationDatabaseAsync(provider);
        var host = await AssistantTestHost.CreateAsync(
            database,
            provider,
            new NhAiScriptedChatClient(),
            configure: services =>
            {
                services.AddSingleton(new TestSubjects());
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, TestPrincipalFactory>());
                services.AddDbContext<OrderDbContext>(options =>
                {
                    if (provider == AssistantTestProvider.SqlServer)
                    {
                        options.UseNewHeapSqlServer(application);
                    }
                    else
                    {
                        options.UseNewHeapPostgreSql(application);
                    }
                });
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
                .AddSubjectType<TestSubjectResolver>()
                .ConfigureAgent(AgentId, agent => agent.Grant("projects.*"))
                .AddTrigger<OrderChanged>(TriggerId, trigger => trigger
                    .StartAgent(AgentId)
                    .Subject(evt => Subject(evt.ProjectId))
                    .Message(evt => "Order " + evt.Change)
                    .Dedupe(evt => evt.Key)
                    .OnBusy(NhAssistantTriggerConflict.Coalesce, TimeSpan.Zero)));
        await using var scope = host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<OrderDbContext>().Database.EnsureCreatedAsync();
        return host;
    }

    private async Task<string> ApplicationDatabaseAsync(AssistantTestProvider provider)
    {
        var name = "orders_" + Guid.NewGuid().ToString("N")[..12];
        var connectionString = await database.ConnectionStringAsync(provider);
        return provider == AssistantTestProvider.SqlServer
            ? new SqlConnectionStringBuilder(connectionString) { InitialCatalog = name }.ConnectionString
            : new NpgsqlConnectionStringBuilder(connectionString) { Database = name }.ConnectionString;
    }

    private static Task<IReadOnlyList<NhAssistantTriggerReceipt>> PublishAsync(AssistantTestHost host, OrderChanged evt, bool commit)
    {
        return PublishAsync(host, [evt], commit);
    }

    /// <summary>
    /// Saves the order changes and publishes their events in one application transaction.
    /// </summary>
    private static async Task<IReadOnlyList<NhAssistantTriggerReceipt>> PublishAsync(
        AssistantTestHost host,
        IReadOnlyList<OrderChanged> events,
        bool commit)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var triggers = scope.ServiceProvider.GetRequiredService<INhAssistantAgentTriggers>();
        await using var transaction = await context.Database.BeginTransactionAsync();
        var receipts = new List<NhAssistantTriggerReceipt>();
        foreach (var evt in events)
        {
            context.Orders.Add(new OrderRow { Id = Guid.NewGuid(), ProjectId = evt.ProjectId, Change = evt.Change });
            await context.SaveChangesAsync();
            receipts.AddRange(await triggers.PublishAsync(evt));
        }

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

    private static async Task<IReadOnlyList<NhAssistantTriggerReceipt>> PublishWithoutTransactionAsync(AssistantTestHost host, OrderChanged evt)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INhAssistantAgentTriggers>().PublishAsync(evt);
    }

    /// <summary>
    /// The deferred triggers of a project that the application committed, oldest first.
    /// </summary>
    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private static async Task<IReadOnlyList<NhAssistantFireTriggerRequest>> DeferredAsync(AssistantTestHost host, Guid projectId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var subjectId = Subject(projectId).Id;
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
    /// Does what the dispatcher does after the commit: runs each deferred trigger of the project
    /// that has not fired yet, in publishing order.
    /// </summary>
    private static async Task<IReadOnlyList<NhAssistantTriggerReceipt>> DeliverAsync(AssistantTestHost host, Guid projectId)
    {
        var receipts = new List<NhAssistantTriggerReceipt>();
        foreach (var request in await DeferredAsync(host, projectId))
        {
            if (!await host.Services.GetRequiredService<NhAssistantRunStore>().QueuedInputExistsAsync(request.InputId, CancellationToken.None))
            {
                receipts.Add(await FireAsync(host, request));
            }
        }

        return receipts;
    }

    private static async Task<NhAssistantTriggerReceipt> FireAsync(AssistantTestHost host, NhAssistantFireTriggerRequest request)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NhAssistantAgentTriggers>()
            .FireDeferredAsync(request, CancellationToken.None);
    }

    private static async Task<bool> OperationExistsAsync(AssistantTestHost host, Guid? operationId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return operationId is { } id
            && await scope.ServiceProvider.GetRequiredService<OrderDbContext>().Set<NhBackgroundOperation>()
                .AnyAsync(operation => operation.Id == id);
    }

    private static Task<IReadOnlyList<AssistantThread>> ThreadsAsync(AssistantTestHost host, Guid projectId)
    {
        return host.Services.GetRequiredService<NhAssistantRunStore>().FindThreadsBySubjectAsync(Subject(projectId), CancellationToken.None);
    }

    private static async Task<IReadOnlyList<AssistantRun>> RunsAsync(AssistantTestHost host, Guid conversationId)
    {
        await using var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        return await context.Runs.AsNoTracking()
            .Where(run => run.ConversationId == conversationId)
            .OrderBy(run => run.CreatedAt)
            .ToListAsync();
    }

    private static async Task<IReadOnlyList<AssistantQueuedInput>> InputsAsync(AssistantTestHost host, Guid conversationId)
    {
        await using var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        return await context.QueuedInputs.AsNoTracking()
            .Where(input => input.ConversationId == conversationId)
            .OrderBy(input => input.CreatedAt)
            .ToListAsync();
    }

    private static NhAiSubjectRef Subject(Guid projectId)
    {
        return new NhAiSubjectRef("project", projectId.ToString("N"));
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

/// <param name="Key">The dedupe key, derived from the changed row and its version.</param>
public sealed record OrderChanged(Guid ProjectId, string? Key, string Change);

internal sealed class OrderRow
{
    public Guid Id { get; set; }

    public Guid ProjectId { get; set; }

    public string Change { get; set; } = "";
}

/// <summary>
/// The application's own context: its orders and the platform's background operations.
/// </summary>
internal sealed class OrderDbContext(DbContextOptions<OrderDbContext> options) : DbContext(options)
{
    public DbSet<OrderRow> Orders => Set<OrderRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OrderRow>().ToTable("Orders");
        modelBuilder.AddNewHeapBackgroundOperations();
    }
}
