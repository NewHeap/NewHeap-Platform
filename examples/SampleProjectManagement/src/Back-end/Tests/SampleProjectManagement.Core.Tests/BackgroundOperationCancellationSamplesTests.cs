using Hangfire;
using Hangfire.InMemory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NewHeap.Platform.AspNet.Common;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.PostgreSql;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using SampleProjectManagement.Api.Jobs;
using SampleProjectManagement.Api.Services;
using SampleProjectManagement.DAL;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// SPM-325: a handler that checks the durable cancellation between its items stops as soon as the
/// owner cancels, and the operation ends cancelled even when the heartbeat has not seen the request
/// yet. The sample's project-analysis child checks <c>ThrowIfCancellationRequestedAsync</c> before
/// every pass; here it runs through Hangfire on the migrated sample database. After its passes the
/// child stores its analysis in <see cref="ProjectAnalysisArchive"/> outside a step, whose client
/// reports a cancelled call as an error of its own: when the owner cancels during that call, the
/// operation still ends cancelled, and nothing retries the error.
/// </summary>
public sealed class BackgroundOperationCancellationSamplesTests(SamplePostgreSqlServer postgreSql)
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    /// <summary>
    /// A heartbeat a minute apart never sees the cancellation before the handler does.
    /// </summary>
    private static readonly TimeSpan SlowHeartbeat = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task A_handler_that_checks_the_durable_cancellation_ends_cancelled_before_the_heartbeat()
    {
        // SPM-325
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgreSql.CreateMigratedDatabaseAsync("operation_cancellation");
        using var hangfireStorage = new InMemoryStorage();
        using var host = BuildHost(connectionString, hangfireStorage, SlowHeartbeat);
        await host.StartAsync(cancellationToken);
        try
        {
            var ownerId = await CreateOwnerAsync(host.Services);
            Guid operationId;
            await using (var scope = host.Services.CreateAsyncScope())
            {
                // Thousands of passes keep the analysis busy far longer than the test waits.
                var enqueued = await scope.ServiceProvider.GetRequiredService<INhBackgroundOperationService>().EnqueueAsync(
                    new ProjectAnalysisChildRequest(Guid.NewGuid(), 10_000, 20, FailFirstAttempt: false),
                    new NhBackgroundOperationEnqueueOptions { OwnerUserId = ownerId },
                    cancellationToken);
                Assert.True(enqueued.Success);
                operationId = enqueued.Data!.Id;
            }

            await WaitForAsync(host.Services, operationId, operation => operation.Steps.Any(step =>
                step.StepKey == "process-passes" && step.ProcessedItems > 0));

            await using (var scope = host.Services.CreateAsyncScope())
            {
                var cancellation = await scope.ServiceProvider.GetRequiredService<INhBackgroundOperationService>()
                    .RequestCancellationAsync(operationId, ownerId, cancellationToken);
                Assert.True(cancellation.Success);
            }

            var ended = await WaitForAsync(host.Services, operationId, operation => operation.CompletedAt is not null);

            // Cancelled, not timed out: the handler saw the request itself at its next pass.
            Assert.Equal(NhBackgroundOperationStatus.Cancelled, ended.Status);
            Assert.Equal("cancelled", ended.FailureCode);
            Assert.Equal("background-operation.cancelled", ended.FailureMessageKey);
            var attempt = Assert.Single(ended.Attempts);
            Assert.Equal(NhBackgroundOperationAttemptStatus.Cancelled, attempt.Status);
            // No heartbeat ran during the attempt: it was the handler's own check that stopped it.
            Assert.Equal(attempt.StartedAt, attempt.HeartbeatAt);
            var passes = ended.Steps.Single(step => step.StepKey == "process-passes");
            Assert.InRange(passes.ProcessedItems, 1, 9_999);
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// The analysis is done and the child is storing it in the archive, outside a step, when the
    /// owner cancels. The heartbeat sees the request and cancels the archive call, and the archive
    /// reports that as an error of its own. The child may retry three times, as the sample registers
    /// it, but the operation ends cancelled with its single attempt, not <c>handler-failed</c> with
    /// a retry.
    /// </summary>
    [Fact]
    public async Task An_error_outside_a_step_while_the_owner_cancels_ends_cancelled_without_a_retry()
    {
        // SPM-325
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgreSql.CreateMigratedDatabaseAsync("operation_cancellation_archive");
        using var hangfireStorage = new InMemoryStorage();
        // The archive answers only after the test would have given up, so the call is still waiting
        // when the owner cancels.
        var archive = new ProjectAnalysisArchive(Patience * 2);
        using var host = BuildHost(connectionString, hangfireStorage, TimeSpan.FromMilliseconds(200), archive);
        await host.StartAsync(cancellationToken);
        try
        {
            var ownerId = await CreateOwnerAsync(host.Services);
            var projectId = Guid.NewGuid();
            Guid operationId;
            await using (var scope = host.Services.CreateAsyncScope())
            {
                var enqueued = await scope.ServiceProvider.GetRequiredService<INhBackgroundOperationService>().EnqueueAsync(
                    new ProjectAnalysisChildRequest(projectId, 3, 1, FailFirstAttempt: false),
                    new NhBackgroundOperationEnqueueOptions { OwnerUserId = ownerId },
                    cancellationToken);
                Assert.True(enqueued.Success);
                operationId = enqueued.Data!.Id;
            }

            // Every pass is done: the child is outside its step, waiting for the archive.
            await WaitForAsync(host.Services, operationId, operation => operation.Steps.Any(step =>
                step.StepKey == "process-passes" && step.Status == NhBackgroundOperationStepStatus.Succeeded));

            await using (var scope = host.Services.CreateAsyncScope())
            {
                var cancellation = await scope.ServiceProvider.GetRequiredService<INhBackgroundOperationService>()
                    .RequestCancellationAsync(operationId, ownerId, cancellationToken);
                Assert.True(cancellation.Success);
            }

            var ended = await WaitForAsync(host.Services, operationId, operation => operation.CompletedAt is not null);

            // Cancelled as the owner asked, not a failure that waits for its retry.
            Assert.Equal(NhBackgroundOperationStatus.Cancelled, ended.Status);
            Assert.Equal("cancelled", ended.FailureCode);
            Assert.Equal("background-operation.cancelled", ended.FailureMessageKey);
            Assert.Null(ended.NextDispatchAt);
            var attempt = Assert.Single(ended.Attempts);
            Assert.Equal(NhBackgroundOperationAttemptStatus.Cancelled, attempt.Status);
            Assert.Equal(
                NhBackgroundOperationStepStatus.Succeeded,
                ended.Steps.Single(step => step.StepKey == "process-passes").Status);
            Assert.Null(archive.FindAnalysedPasses(projectId));
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// The background-operation and Hangfire modules on the sample's own context, with heartbeats
    /// <paramref name="heartbeatInterval"/> apart and, when given, the project archive.
    /// </summary>
    private static IHost BuildHost(
        string connectionString,
        InMemoryStorage hangfireStorage,
        TimeSpan heartbeatInterval,
        ProjectAnalysisArchive? archive = null)
    {
        // A worker host without a web server: the module's controllers stay unmapped.
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddDbContext<SampleProjectManagementDbContext>(options =>
            options.UseNewHeapPostgreSql(connectionString));
        builder.Services.AddNewHeapHangfireProcessing(
            hangfire => hangfire.UseStorage(hangfireStorage),
            backgroundJobServerOptions: server =>
            {
                server.ServerName = "operation-cancellation-" + Guid.NewGuid().ToString("N");
                server.WorkerCount = 2;
                server.SchedulePollingInterval = TimeSpan.FromMilliseconds(100);
            });
        builder.Services.AddNewHeapBackgroundOperations<SampleProjectManagementDbContext>(operations =>
        {
            operations.Options.DispatchInterval = TimeSpan.FromMilliseconds(200);
            operations.Options.HeartbeatInterval = heartbeatInterval;
            operations.Options.DefaultLeaseDuration = heartbeatInterval * 2;
            operations.Options.StaleAttemptTimeout = heartbeatInterval * 5;
            operations.Options.LiveUpdatesEnabled = false;
            operations.Options.UserNotificationProjectionEnabled = false;
            operations.Options.CleanupEnabled = false;
            operations.Add<ProjectAnalysisChildRequest, ProjectAnalysisChildOperation>(
                "sample-project-analysis-child");
        });
        if (archive is not null)
        {
            builder.Services.AddSingleton(archive);
        }

        return builder.Build();
    }

    private static async Task<Guid> CreateOwnerAsync(IServiceProvider services)
    {
        var ownerId = Guid.NewGuid();
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<SampleProjectManagementDbContext>();
        dbContext.Users.Add(new NhUser
        {
            Id = ownerId,
            UserName = $"analyst-{ownerId:N}@example.test",
            NormalizedUserName = $"ANALYST-{ownerId:N}@EXAMPLE.TEST"
        });
        await dbContext.SaveChangesAsync();
        return ownerId;
    }

    /// <summary>
    /// Polls the operation until the condition holds; the bound only matters when the test fails.
    /// </summary>
    private static async Task<NhBackgroundOperation> WaitForAsync(
        IServiceProvider services,
        Guid operationId,
        Func<NhBackgroundOperation, bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow + Patience;
        NhBackgroundOperation? operation = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using (var scope = services.CreateAsyncScope())
            {
                operation = await scope.ServiceProvider.GetRequiredService<SampleProjectManagementDbContext>()
                    .Set<NhBackgroundOperation>()
                    .AsNoTracking()
                    .Include(item => item.Steps)
                    .Include(item => item.Attempts)
                    .AsSplitQuery()
                    .SingleAsync(item => item.Id == operationId);
            }

            if (condition(operation))
            {
                return operation;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        throw new TimeoutException($"Background operation {operationId} stayed {operation?.Status}.");
    }
}
