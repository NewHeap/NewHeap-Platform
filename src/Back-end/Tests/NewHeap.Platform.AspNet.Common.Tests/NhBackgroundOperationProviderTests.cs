using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models.Mutate;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NewHeap.Platform.AspNet.Common.Services.Notification;
using NewHeap.Platform.AspNet.Common.Utilities;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Utilities;
using NewHeap.Platform.Mapping;
using NSubstitute;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

public sealed class NhBackgroundOperationProviderTests
{
    [Fact]
    public async Task PersistenceLeasesAndFencingWorkOnBothRelationalProviders()
    {
        await using (var sqlServer = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04").Build())
        {
            await sqlServer.StartAsync();
            await VerifyProviderAsync(options => options.UseSqlServer(sqlServer.GetConnectionString()), "sql-server");
        }

        await using (var postgreSql = new PostgreSqlBuilder("postgres:15.1").Build())
        {
            await postgreSql.StartAsync();
            await VerifyProviderAsync(options => options.UseNpgsql(postgreSql.GetConnectionString()), "postgresql");
        }
    }

    private static async Task VerifyProviderAsync(
        Action<DbContextOptionsBuilder> configureProvider,
        string providerName)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<BackgroundOperationDbContext>(configureProvider);
        services.AddScoped<IRepository<NhBackgroundOperation>>(serviceProvider =>
            new Repository<NhBackgroundOperation>(serviceProvider.GetRequiredService<BackgroundOperationDbContext>(), serviceProvider));
        services.AddScoped<IRepository<NhBackgroundOperationLease>>(serviceProvider =>
            new Repository<NhBackgroundOperationLease>(serviceProvider.GetRequiredService<BackgroundOperationDbContext>(), serviceProvider));
        services.AddScoped<IRepository<NhBackgroundOperationAttempt>>(serviceProvider =>
            new Repository<NhBackgroundOperationAttempt>(serviceProvider.GetRequiredService<BackgroundOperationDbContext>(), serviceProvider));
        services.AddScoped<ProviderExpectedFailureHandler>();
        services.AddScoped<ProviderRetryResultHandler>();
        services.AddScoped<ProviderCancellationHandler>();
        services.AddScoped<ProviderFanInParentHandler>();
        services.AddScoped<ProviderFanInChildHandler>();
        services.AddSingleton<INhBackgroundOperationScheduler, NoOpScheduler>();
        var notificationService = Substitute.For<INhUserNotificationService>();
        services.AddSingleton(notificationService);
        services.AddSingleton<INhBackgroundOperationNotificationFormatter,
            NhDefaultBackgroundOperationNotificationFormatter>();
        await using var serviceProvider = services.BuildServiceProvider();

        var ownerId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var firstAttemptId = Guid.NewGuid();
        var secondAttemptId = Guid.NewGuid();
        await using (var seedScope = serviceProvider.CreateAsyncScope())
        {
            var context = seedScope.ServiceProvider.GetRequiredService<BackgroundOperationDbContext>();
            await context.Database.EnsureCreatedAsync();
            context.Users.Add(new NhUser
            {
                Id = ownerId,
                UserName = $"{providerName}@example.test",
                NormalizedUserName = $"{providerName.ToUpperInvariant()}@EXAMPLE.TEST"
            });
            var operation = new NhBackgroundOperation
            {
                Id = operationId,
                OperationType = "provider-test",
                PayloadJson = "{}",
                OwnerUserId = ownerId,
                Status = NhBackgroundOperationStatus.Running,
                DispatchGeneration = 1,
                CurrentAttemptId = firstAttemptId,
                CurrentAttemptNumber = 1,
                Version = 1
            };
            operation.Steps.Add(new NhBackgroundOperationStep
            {
                Id = Guid.NewGuid(),
                OperationId = operationId,
                StepKey = "root",
                Status = NhBackgroundOperationStepStatus.Running,
                AggregationMode = NhBackgroundOperationAggregationMode.Manual,
                FencingVersion = 1,
                Version = 1
            });
            operation.Attempts.Add(new NhBackgroundOperationAttempt
            {
                Id = firstAttemptId,
                OperationId = operationId,
                AttemptNumber = 1,
                DispatchGeneration = 1,
                Status = NhBackgroundOperationAttemptStatus.Running,
                Version = 1
            });
            context.BackgroundOperations.Add(operation);
            context.BackgroundOperationIdempotencyRecords.Add(new NhBackgroundOperationIdempotencyRecord
            {
                Scope = "provider-test",
                KeyHash = new string('a', 64),
                OperationId = operationId
            });
            await context.SaveChangesAsync();
        }

        var options = new NhBackgroundOperationsOptions
        {
            UserNotificationProjectionEnabled = false,
            LiveUpdatesEnabled = false,
            DispatchInterval = TimeSpan.FromMilliseconds(50),
            HeartbeatInterval = TimeSpan.FromMilliseconds(100),
            DefaultLeaseDuration = TimeSpan.FromSeconds(2),
            PayloadRetentionPeriod = TimeSpan.FromDays(1),
            EventRetentionPeriod = TimeSpan.FromDays(1),
            SucceededRetentionPeriod = TimeSpan.FromDays(2),
            CancelledRetentionPeriod = TimeSpan.FromDays(2),
            FailedRetentionPeriod = TimeSpan.FromDays(3),
            TransactionLockTimeoutMilliseconds = 250
        };
        var firstClaim = new NhBackgroundOperationAttemptClaim(
            operationId, firstAttemptId, 1, 1, 1, "provider-test", 1, "{}", "default", "provider-resource", ownerId);
        var firstManager = new NhBackgroundOperationLeaseManager(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(), options, firstClaim);

        var registryBuilder = new NhBackgroundOperationBuilder(new ServiceCollection(), options);
        registryBuilder.Add<ProviderParentRequest, ProviderParentHandler>("provider-parent");
        registryBuilder.Add<ProviderChildRequest, ProviderChildHandler>("provider-child");
        registryBuilder.Add<ProviderExpectedFailureRequest, ProviderExpectedFailureHandler>(
            "provider-expected-failure",
            operation => operation.WithRetry(2));
        registryBuilder.Add<ProviderRetryResultRequest, ProviderRetryResultHandler>(
            "provider-retry-result",
            operation => operation.WithRetry(2));
        registryBuilder.Add<ProviderCancellationRequest, ProviderCancellationHandler>(
            "provider-cancellation");
        registryBuilder.Add<ProviderFanInParentRequest, ProviderFanInParentHandler>(
            "provider-fan-in-parent");
        registryBuilder.Add<ProviderFanInChildRequest, ProviderFanInChildHandler>(
            "provider-fan-in-child");
        var registry = registryBuilder.Build();
        var fanOutCoordinator = new NhBackgroundOperationFanOutCoordinator(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            registry,
            options,
            new NhHangfireQueueNameResolver(),
            new NoOpLiveUpdatePublisher(),
            new NoOpNotificationProjector(),
            NullLogger<NhBackgroundOperationFanOutCoordinator>.Instance);

        var persistence = new NhBackgroundOperationPersistence(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            options,
            new NoOpLiveUpdatePublisher(),
            new NoOpNotificationProjector(),
            fanOutCoordinator,
            NullLogger<NhBackgroundOperationPersistence>.Instance);
        await VerifyOperationLockContentionAsync(
            serviceProvider,
            persistence,
            firstClaim);
        var operationContext = new NhBackgroundOperationContext(
            firstClaim,
            persistence,
            options,
            firstManager,
            fanOutCoordinator);
        await operationContext.Progress.DefineAsync(plan => plan
            .Step("provider-phase", 1, "provider.phase"));
        await operationContext.Progress.RunStepAsync("provider-phase", async (phase, cancellationToken) =>
        {
            var batchResult = await phase.RunStepAsync(
                "provider-batch",
                1,
                "provider.batch",
                async (batchStep, nestedCancellationToken) =>
                {
                    await using var batch = await batchStep.BeginBatchAsync(
                        3,
                        new NhBackgroundOperationBatchOptions
                        {
                            FlushEveryItems = 1,
                            FlushInterval = TimeSpan.FromMilliseconds(1)
                        },
                        nestedCancellationToken);
                    for (var index = 0; index < 3; index++)
                    {
                        await batch.ItemStartedAsync(nestedCancellationToken);
                        await batch.ItemSucceededAsync(nestedCancellationToken);
                    }

                    return TaskResult.Succeeded();
                },
                cancellationToken);
            if (!batchResult.Success)
            {
                return batchResult;
            }

            await phase.ReportAsync(
                1,
                1,
                "provider.phase.completed",
                new
                {
                    count = 3
                },
                cancellationToken);
            return TaskResult.Succeeded();
        });

        var checkpointResult = await operationContext.Checkpoints.SetAsync(
            "provider-checkpoint",
            new
            {
                Value = 1
            },
            expectedVersion: 0);
        checkpointResult.Success.Should().BeTrue();

        var checkpointConflict = await operationContext.Checkpoints.SetAsync(
            "provider-checkpoint",
            new
            {
                Value = 2
            },
            expectedVersion: 0);
        checkpointConflict.Success.Should().BeFalse();
        checkpointConflict.GetResultItems().Single().Name.Should().Be("checkpoint-version-conflict");

        await persistence.CompleteAsync(
            firstClaim,
            NhBackgroundOperationStatus.Succeeded,
            null,
            null,
            null,
            null,
            CancellationToken.None);

        var firstLease = await firstManager.AcquireAsync("provider-resource");
        firstLease.Should().NotBeNull();

        var secondClaim = firstClaim with
        {
            AttemptId = secondAttemptId,
            AttemptNumber = 2
        };
        var secondManager = new NhBackgroundOperationLeaseManager(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(), options, secondClaim);
        (await secondManager.AcquireAsync("provider-resource")).Should().BeNull();
        var requiredAction = () => secondManager.AcquireRequiredAsync("provider-resource");
        (await requiredAction.Should().ThrowAsync<NhBackgroundOperationContentionSignal>())
            .Which.RetryAfter.Should().Be(TimeSpan.FromSeconds(2));

        var firstFence = firstLease!.FencingToken;
        await firstLease.DisposeAsync();

        var secondLeaseSet = await secondManager.AcquireManyAsync(["provider-resource"]);
        secondLeaseSet.Should().NotBeNull();
        secondLeaseSet!.Leases.Single().FencingToken.Should().BeGreaterThan(firstFence);

        var thirdClaim = secondClaim with
        {
            AttemptId = Guid.NewGuid(),
            AttemptNumber = 3
        };
        var thirdManager = new NhBackgroundOperationLeaseManager(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(), options, thirdClaim);
        (await thirdManager.AcquireAsync("provider-resource")).Should().BeNull();
        await secondLeaseSet.DisposeAsync();

        var secondLease = await secondManager.AcquireAsync("provider-resource");
        secondLease.Should().NotBeNull();
        secondLease!.FencingToken.Should().BeGreaterThan(firstFence);
        await secondLease.DisposeAsync();

        await using (var verificationScope = serviceProvider.CreateAsyncScope())
        {
            var verificationContext = verificationScope.ServiceProvider.GetRequiredService<BackgroundOperationDbContext>();
            var persistedOperation = await verificationContext.BackgroundOperations.AsNoTracking().SingleAsync();
            persistedOperation.OperationType.Should().Be("provider-test");
            persistedOperation.Status.Should().Be(NhBackgroundOperationStatus.Succeeded);
            persistedOperation.ProgressPercentage.Should().Be(100);
            persistedOperation.LatestEventSequence.Should().BeGreaterThan(0);
            var persistedSteps = await verificationContext.BackgroundOperationSteps.AsNoTracking().ToListAsync();
            persistedSteps.Should().HaveCount(3);
            persistedSteps.Single(step => step.StepKey == "provider-batch").Should().Match<NhBackgroundOperationStep>(step =>
                step.ProcessedItems == 3
                && step.SucceededItems == 3
                && step.Percentage == 100
                && step.TitleKey == "provider.batch");
            var completedPhaseEvent = await verificationContext.BackgroundOperationEvents.AsNoTracking()
                .SingleAsync(operationEvent =>
                    operationEvent.EventType == NhBackgroundOperationEventType.StepCompleted
                    && operationEvent.MessageKey == "provider.phase.completed");
            completedPhaseEvent.MessageArgumentsJson.Should().Be("{\"count\":3}");
            (await verificationContext.BackgroundOperationLeases.AsNoTracking().SingleAsync()).FencingToken.Should().Be(3);
        }

        await VerifyDivisionQueryIsolationAsync(
            serviceProvider,
            options,
            registry,
            ownerId);
        await VerifyEventRetentionAsync(serviceProvider, ownerId);
        await VerifyNotificationProjectionRetryAsync(
            serviceProvider,
            options,
            notificationService,
            ownerId);

        var healthCheck = new NhBackgroundOperationHealthCheck(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            options);
        (await healthCheck.CheckHealthAsync(new HealthCheckContext())).Status.Should().Be(HealthStatus.Healthy);

        var cleanup = new NhBackgroundOperationCleanupService(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            options,
            NullLogger<NhBackgroundOperationCleanupService>.Instance);
        var redactionResult = await cleanup.CleanupAsync(utcNow: DateTimeOffset.UtcNow.AddDays(1.5));
        redactionResult.RedactedOperations.Should().Be(1);
        redactionResult.RemovedOperations.Should().Be(0);
        await using (var redactionScope = serviceProvider.CreateAsyncScope())
        {
            var context = redactionScope.ServiceProvider.GetRequiredService<BackgroundOperationDbContext>();
            (await context.BackgroundOperations.AsNoTracking().SingleAsync()).PayloadJson.Should().Be("{}");
        }

        var removalResult = await cleanup.CleanupAsync(utcNow: DateTimeOffset.UtcNow.AddDays(2.5));
        removalResult.RemovedOperations.Should().Be(1);
        await using var cleanupVerificationScope = serviceProvider.CreateAsyncScope();
        var cleanupContext = cleanupVerificationScope.ServiceProvider.GetRequiredService<BackgroundOperationDbContext>();
        (await cleanupContext.BackgroundOperations.AsNoTracking().CountAsync()).Should().Be(0);
        (await cleanupContext.BackgroundOperationIdempotencyRecords.AsNoTracking().CountAsync()).Should().Be(0);
        var retainedLease = await cleanupContext.BackgroundOperationLeases.AsNoTracking().SingleAsync();
        retainedLease.OperationId.Should().BeNull();
        retainedLease.FencingToken.Should().Be(3);

        await VerifyFanOutAsync(
            serviceProvider,
            options,
            persistence,
            fanOutCoordinator,
            registry,
            ownerId);
    }

    private static async Task VerifyOperationLockContentionAsync(
        ServiceProvider serviceProvider,
        NhBackgroundOperationPersistence persistence,
        NhBackgroundOperationAttemptClaim claim)
    {
        await using var lockScope = serviceProvider.CreateAsyncScope();
        var repository = lockScope.ServiceProvider.GetRequiredService<IRepository<NhBackgroundOperation>>();
        await using var transaction = await repository.StartOrGetTransactionScopeAsync();
        var acquired = await repository.TryAcquireTransactionLockAsync(
            transaction,
            $"NhBackgroundOperation:Operation:{claim.OperationId:N}",
            250);
        acquired.Should().BeTrue();

        var action = async () =>
        {
            await persistence.HeartbeatAsync(claim, CancellationToken.None);
        };

        await action.Should().ThrowAsync<NhBackgroundOperationContentionSignal>();

        var completeAction = async () =>
        {
            await persistence.CompleteAsync(
                claim,
                NhBackgroundOperationStatus.Succeeded,
                null,
                null,
                null,
                null,
                CancellationToken.None);
        };

        await completeAction.Should().ThrowAsync<NhBackgroundOperationContentionSignal>();
    }

    private static async Task VerifyDivisionQueryIsolationAsync(
        ServiceProvider serviceProvider,
        NhBackgroundOperationsOptions options,
        NhBackgroundOperationRegistry registry,
        Guid ownerId)
    {
        var firstDivisionId = Guid.NewGuid();
        var secondDivisionId = Guid.NewGuid();
        var globalOperationId = Guid.NewGuid();
        var firstDivisionOperationId = Guid.NewGuid();
        var secondDivisionOperationId = Guid.NewGuid();
        await using var scope = serviceProvider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<BackgroundOperationDbContext>();
        context.Divisions.AddRange(
            new NhDivision { Id = firstDivisionId, Name = "Provider division one" },
            new NhDivision { Id = secondDivisionId, Name = "Provider division two" });
        context.BackgroundOperations.AddRange(
            CreateQueryableOperation(globalOperationId, ownerId, null),
            CreateQueryableOperation(firstDivisionOperationId, ownerId, firstDivisionId),
            CreateQueryableOperation(secondDivisionOperationId, ownerId, secondDivisionId));
        await context.SaveChangesAsync();

        var service = new NhBackgroundOperationService(
            scope.ServiceProvider.GetRequiredService<IRepository<NhBackgroundOperation>>(),
            registry,
            options,
            new NhHangfireQueueNameResolver(),
            new NoOpScheduler(),
            new NoOpLiveUpdatePublisher(),
            new NoOpNotificationProjector(),
            new Mapper(new MapperConfiguration(configuration =>
                configuration.AddProfile<AutomapperProfileConfiguration>())),
            NullLogger<NhBackgroundOperationService>.Instance);

        var firstDivisionIds = await service.QueryForOwner(ownerId, firstDivisionId)
            .Select(operation => operation.Id)
            .ToListAsync();
        firstDivisionIds.Should().Contain(globalOperationId);
        firstDivisionIds.Should().Contain(firstDivisionOperationId);
        firstDivisionIds.Should().NotContain(secondDivisionOperationId);

        var globalIds = await service.QueryForOwner(ownerId)
            .Select(operation => operation.Id)
            .ToListAsync();
        globalIds.Should().Contain(globalOperationId);
        globalIds.Should().NotContain(firstDivisionOperationId);
        globalIds.Should().NotContain(secondDivisionOperationId);

        context.ChangeTracker.Clear();
        var firstDivision = await context.Divisions.SingleAsync(division => division.Id == firstDivisionId);
        context.Divisions.Remove(firstDivision);
        var deleteDivision = async () =>
        {
            await context.SaveChangesAsync();
        };
        await deleteDivision.Should().ThrowAsync<DbUpdateException>();
        context.ChangeTracker.Clear();

        context.BackgroundOperations.RemoveRange(
            await context.BackgroundOperations
                .Where(operation => operation.Id == globalOperationId
                                    || operation.Id == firstDivisionOperationId
                                    || operation.Id == secondDivisionOperationId)
                .ToListAsync());
        await context.SaveChangesAsync();
        context.Divisions.RemoveRange(
            await context.Divisions
                .Where(division => division.Id == firstDivisionId || division.Id == secondDivisionId)
                .ToListAsync());
        await context.SaveChangesAsync();
    }

    private static async Task VerifyEventRetentionAsync(
        ServiceProvider serviceProvider,
        Guid ownerId)
    {
        var operationId = Guid.NewGuid();
        var retentionOptions = new NhBackgroundOperationsOptions
        {
            MaxEventsPerOperation = 2
        };
        await using var scope = serviceProvider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<BackgroundOperationDbContext>();
        var operation = CreateQueryableOperation(operationId, ownerId, null);
        NhBackgroundOperationService.AppendEvent(
            operation,
            NhBackgroundOperationEventType.StateChanged,
            NhBackgroundOperationMessageSeverity.Information,
            "background-operation.queued",
            null,
            true);
        NhBackgroundOperationService.AppendEvent(
            operation,
            NhBackgroundOperationEventType.Message,
            NhBackgroundOperationMessageSeverity.Information,
            "background-operation.message",
            null,
            false);
        operation.LastProjectedNotificationEventSequence = 1;
        context.BackgroundOperations.Add(operation);
        await context.SaveChangesAsync();

        NhBackgroundOperationService.AppendEvent(
            operation,
            NhBackgroundOperationEventType.ResultAvailable,
            NhBackgroundOperationMessageSeverity.Success,
            "background-operation.result-available",
            null,
            true);
        var repository = scope.ServiceProvider.GetRequiredService<IRepository<NhBackgroundOperation>>();
        await NhBackgroundOperationEventRetention.TrimAsync(
            repository,
            operation,
            retentionOptions,
            CancellationToken.None);
        await context.SaveChangesAsync();

        var retainedEvents = await context.BackgroundOperationEvents
            .AsNoTracking()
            .Where(operationEvent => operationEvent.OperationId == operationId)
            .OrderBy(operationEvent => operationEvent.Sequence)
            .ToListAsync();
        retainedEvents.Should().HaveCount(2);
        retainedEvents.Select(operationEvent => operationEvent.Sequence).Should().Equal(2, 3);
        retainedEvents.Single(operationEvent => operationEvent.Sequence == 3).IsMilestone.Should().BeTrue();

        context.BackgroundOperations.Remove(operation);
        await context.SaveChangesAsync();
    }

    private static NhBackgroundOperation CreateQueryableOperation(
        Guid operationId,
        Guid ownerId,
        Guid? divisionId)
    {
        return new NhBackgroundOperation
        {
            Id = operationId,
            OperationType = "provider-query-test",
            PayloadJson = "{}",
            OwnerUserId = ownerId,
            DivisionId = divisionId,
            Status = NhBackgroundOperationStatus.Succeeded,
            CompletedAt = DateTimeOffset.UtcNow,
            Version = 1
        };
    }

    private static async Task VerifyNotificationProjectionRetryAsync(
        ServiceProvider serviceProvider,
        NhBackgroundOperationsOptions options,
        INhUserNotificationService notificationService,
        Guid ownerId)
    {
        var operationId = Guid.NewGuid();
        var notification = new NhUserNotification
        {
            Id = Guid.NewGuid(),
            UserId = ownerId,
            LastTitle = "Provider notification",
            LastMessage = "Provider notification"
        };
        await using (var seedScope = serviceProvider.CreateAsyncScope())
        {
            var context = seedScope.ServiceProvider.GetRequiredService<BackgroundOperationDbContext>();
            var operation = CreateQueryableOperation(operationId, ownerId, null);
            NhBackgroundOperationService.AppendEvent(
                operation,
                NhBackgroundOperationEventType.StateChanged,
                NhBackgroundOperationMessageSeverity.Success,
                "background-operation.succeeded",
                null,
                true);
            context.UserNotifications.Add(notification);
            context.BackgroundOperations.Add(operation);
            await context.SaveChangesAsync();
        }

        notificationService.CreateAsync(
                Arg.Any<NhUserNotificationMutateModel>(),
                Arg.Any<Guid?>(),
                Arg.Any<Action<NhUserNotification>?>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<BaseDbEntityServiceOperationOptions?>())
            .Returns(
                Task.FromResult(TaskResult<NhUserNotification?>.Failed(
                    "notification-temporarily-unavailable",
                    "background-operation.notification-temporarily-unavailable")),
                Task.FromResult(TaskResult<NhUserNotification?>.Succeeded(notification)));

        var projector = new NhBackgroundOperationNotificationProjector(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            options);
        var firstResult = await projector.ProjectAsync(operationId);
        firstResult.Success.Should().BeFalse();
        await using (var failedScope = serviceProvider.CreateAsyncScope())
        {
            var context = failedScope.ServiceProvider.GetRequiredService<BackgroundOperationDbContext>();
            var failedOperation = await context.BackgroundOperations
                .AsNoTracking()
                .SingleAsync(operation => operation.Id == operationId);
            failedOperation.LastProjectedNotificationEventSequence.Should().Be(0);
            failedOperation.UserNotificationId.Should().BeNull();
        }

        var retryResult = await projector.ProjectAsync(operationId);
        retryResult.Success.Should().BeTrue();
        await using var verificationScope = serviceProvider.CreateAsyncScope();
        var verificationContext = verificationScope.ServiceProvider.GetRequiredService<BackgroundOperationDbContext>();
        var projectedOperation = await verificationContext.BackgroundOperations
            .SingleAsync(operation => operation.Id == operationId);
        projectedOperation.LastProjectedNotificationEventSequence.Should().Be(1);
        projectedOperation.UserNotificationId.Should().Be(notification.Id);

        verificationContext.BackgroundOperations.Remove(projectedOperation);
        verificationContext.UserNotifications.Remove(
            await verificationContext.UserNotifications.SingleAsync(item => item.Id == notification.Id));
        await verificationContext.SaveChangesAsync();
    }

    private static async Task VerifyFanOutAsync(
        ServiceProvider serviceProvider,
        NhBackgroundOperationsOptions options,
        NhBackgroundOperationPersistence persistence,
        NhBackgroundOperationFanOutCoordinator fanOutCoordinator,
        NhBackgroundOperationRegistry registry,
        Guid ownerId)
    {
        var parentId = Guid.NewGuid();
        var firstAttemptId = Guid.NewGuid();
        await using (var seedScope = serviceProvider.CreateAsyncScope())
        {
            var context = seedScope.ServiceProvider.GetRequiredService<BackgroundOperationDbContext>();
            var parent = new NhBackgroundOperation
            {
                Id = parentId,
                OperationType = "provider-parent",
                PayloadJson = "{}",
                OwnerUserId = ownerId,
                Status = NhBackgroundOperationStatus.Running,
                DispatchGeneration = 1,
                CurrentAttemptId = firstAttemptId,
                CurrentAttemptNumber = 1,
                Version = 1
            };
            parent.Steps.Add(new NhBackgroundOperationStep
            {
                Id = Guid.NewGuid(),
                OperationId = parentId,
                StepKey = "root",
                Status = NhBackgroundOperationStepStatus.Running,
                AggregationMode = NhBackgroundOperationAggregationMode.Manual,
                FencingVersion = 1,
                Version = 1
            });
            parent.Attempts.Add(new NhBackgroundOperationAttempt
            {
                Id = firstAttemptId,
                OperationId = parentId,
                AttemptNumber = 1,
                DispatchGeneration = 1,
                Status = NhBackgroundOperationAttemptStatus.Running,
                Version = 1
            });
            context.BackgroundOperations.Add(parent);
            await context.SaveChangesAsync();
        }

        var firstClaim = new NhBackgroundOperationAttemptClaim(
            parentId,
            firstAttemptId,
            1,
            1,
            1,
            "provider-parent",
            1,
            "{}",
            "default",
            null,
            ownerId);
        var leaseManager = new NhBackgroundOperationLeaseManager(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            options,
            firstClaim);
        var operationContext = new NhBackgroundOperationContext(
            firstClaim,
            persistence,
            options,
            leaseManager,
            fanOutCoordinator);
        var items = Enumerable.Range(1, 3)
            .Select(index => NhBackgroundOperationFanOut.Item(
                $"item-{index}",
                new ProviderChildRequest($"value-{index}")))
            .ToArray();

        var suspend = () => operationContext.FanOut.RunAsync("provider-children", items);
        await suspend.Should().ThrowAsync<NhBackgroundOperationFanOutPendingException>();

        Guid[] childIds;
        await using (var suspendedScope = serviceProvider.CreateAsyncScope())
        {
            var context = suspendedScope.ServiceProvider.GetRequiredService<BackgroundOperationDbContext>();
            var parent = await context.BackgroundOperations.AsNoTracking().SingleAsync(x => x.Id == parentId);
            parent.Status.Should().Be(NhBackgroundOperationStatus.WaitingForChildren);
            parent.NextDispatchAt.Should().NotBeNull();
            (await context.BackgroundOperationAttempts.AsNoTracking().SingleAsync(x => x.Id == firstAttemptId))
                .Status.Should().Be(NhBackgroundOperationAttemptStatus.Suspended);
            var children = await context.BackgroundOperations.AsNoTracking()
                .Where(x => x.ParentOperationId == parentId)
                .OrderBy(x => x.FanOutItemKey)
                .ToListAsync();
            children.Should().HaveCount(3);
            children.Should().OnlyContain(x =>
                x.RootOperationId == parentId
                && x.Status == NhBackgroundOperationStatus.PendingDispatch
                && x.OperationType == "provider-child");
            childIds = children.Select(x => x.Id).ToArray();
        }

        foreach (var childId in childIds)
        {
            await using (var childScope = serviceProvider.CreateAsyncScope())
            {
                var context = childScope.ServiceProvider.GetRequiredService<BackgroundOperationDbContext>();
                var child = await context.BackgroundOperations.SingleAsync(x => x.Id == childId);
                child.Status = NhBackgroundOperationStatus.Succeeded;
                child.ProgressPercentage = 100;
                child.CompletedAt = DateTimeOffset.UtcNow;
                child.NextDispatchAt = null;
                child.Version++;
                await context.SaveChangesAsync();
            }
            await fanOutCoordinator.OperationChangedAsync(childId, CancellationToken.None);
        }

        await using (var wakeScope = serviceProvider.CreateAsyncScope())
        {
            var context = wakeScope.ServiceProvider.GetRequiredService<BackgroundOperationDbContext>();
            var parent = await context.BackgroundOperations.SingleAsync(x => x.Id == parentId);
            parent.Status.Should().Be(NhBackgroundOperationStatus.PendingDispatch);
            var fanOutStep = await context.BackgroundOperationSteps.AsNoTracking()
                .SingleAsync(x => x.OperationId == parentId && x.StepKey == "provider-children");
            fanOutStep.AggregationMode.Should().Be(NhBackgroundOperationAggregationMode.ChildOperations);
            fanOutStep.ProcessedItems.Should().Be(3);
            fanOutStep.SucceededItems.Should().Be(3);
            fanOutStep.ActiveItems.Should().Be(0);
            fanOutStep.Percentage.Should().Be(100);

            parent.Status = NhBackgroundOperationStatus.Queued;
            parent.DispatchGeneration = 2;
            await context.SaveChangesAsync();
        }

        var resumedClaim = await persistence.TryStartAttemptAsync(parentId, 2, CancellationToken.None);
        resumedClaim.Should().NotBeNull();
        var resumedLeaseManager = new NhBackgroundOperationLeaseManager(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            options,
            resumedClaim!);
        var resumedContext = new NhBackgroundOperationContext(
            resumedClaim!,
            persistence,
            options,
            resumedLeaseManager,
            fanOutCoordinator);
        var result = await resumedContext.FanOut.RunAsync("provider-children", items);
        result.Success.Should().BeTrue();
        result.Data.Should().NotBeNull();
        result.Data!.Total.Should().Be(3);
        result.Data.Succeeded.Should().Be(3);
        result.Data.HasFailures.Should().BeFalse();

        var failingItems = new[]
        {
            NhBackgroundOperationFanOut.Item(
                "failing-item",
                new ProviderChildRequest("failure"))
        };
        var suspendForFailure = () => resumedContext.FanOut.RunAsync("provider-failing-child", failingItems);
        await suspendForFailure.Should().ThrowAsync<NhBackgroundOperationFanOutPendingException>();

        Guid failingChildId;
        await using (var failureScope = serviceProvider.CreateAsyncScope())
        {
            var context = failureScope.ServiceProvider.GetRequiredService<BackgroundOperationDbContext>();
            var child = await context.BackgroundOperations
                .SingleAsync(x => x.ParentOperationId == parentId && x.FanOutKey == "provider-failing-child");
            failingChildId = child.Id;
            child.Status = NhBackgroundOperationStatus.Failed;
            child.FailureCode = "expected-provider-test-failure";
            child.ProgressPercentage = 100;
            child.CompletedAt = DateTimeOffset.UtcNow;
            child.NextDispatchAt = null;
            child.Version++;
            await context.SaveChangesAsync();
        }
        await fanOutCoordinator.OperationChangedAsync(failingChildId, CancellationToken.None);

        await using (var secondWakeScope = serviceProvider.CreateAsyncScope())
        {
            var context = secondWakeScope.ServiceProvider.GetRequiredService<BackgroundOperationDbContext>();
            var parent = await context.BackgroundOperations.SingleAsync(x => x.Id == parentId);
            parent.Status.Should().Be(NhBackgroundOperationStatus.PendingDispatch);
            parent.Status = NhBackgroundOperationStatus.Queued;
            parent.DispatchGeneration = 3;
            await context.SaveChangesAsync();
        }

        var finalClaim = await persistence.TryStartAttemptAsync(parentId, 3, CancellationToken.None);
        finalClaim.Should().NotBeNull();
        var finalLeaseManager = new NhBackgroundOperationLeaseManager(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            options,
            finalClaim!);
        var finalContext = new NhBackgroundOperationContext(
            finalClaim!,
            persistence,
            options,
            finalLeaseManager,
            fanOutCoordinator);
        var successfulFanOut = await finalContext.FanOut.RunAsync("provider-children", items);
        successfulFanOut.Success.Should().BeTrue();
        successfulFanOut.Data.Should().NotBeNull();
        successfulFanOut.Data!.HasFailures.Should().BeFalse();
        var failedFanOut = await finalContext.FanOut.RunAsync("provider-failing-child", failingItems);
        failedFanOut.Success.Should().BeFalse();
        failedFanOut.Data.Should().NotBeNull();
        failedFanOut.Data!.Failed.Should().Be(1);
        await persistence.CompleteAsync(
            finalClaim!,
            NhBackgroundOperationStatus.Failed,
            "child-operation-failed",
            "background-operation.child-operation-failed",
            null,
            null,
            CancellationToken.None);

        await using var verificationScope = serviceProvider.CreateAsyncScope();
        var verificationContext = verificationScope.ServiceProvider.GetRequiredService<BackgroundOperationDbContext>();
        (await verificationContext.BackgroundOperations.AsNoTracking().SingleAsync(x => x.Id == parentId))
            .Status.Should().Be(NhBackgroundOperationStatus.Failed);
        (await verificationContext.BackgroundOperationAttempts.AsNoTracking()
            .Where(x => x.OperationId == parentId)
            .OrderBy(x => x.AttemptNumber)
            .Select(x => x.Status)
            .ToListAsync()).Should().Equal(
            NhBackgroundOperationAttemptStatus.Suspended,
            NhBackgroundOperationAttemptStatus.Suspended,
            NhBackgroundOperationAttemptStatus.Failed);

        await VerifyHierarchyRetryAndCancellationAsync(
            serviceProvider,
            options,
            registry,
            parentId,
            ownerId);

        var cleanup = new NhBackgroundOperationCleanupService(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            options,
            NullLogger<NhBackgroundOperationCleanupService>.Instance);
        var firstCleanup = await cleanup.CleanupAsync(utcNow: DateTimeOffset.UtcNow.AddDays(2.5));
        firstCleanup.RemovedOperations.Should().Be(3);
        var secondCleanup = await cleanup.CleanupAsync(utcNow: DateTimeOffset.UtcNow.AddDays(2.5));
        secondCleanup.RemovedOperations.Should().Be(0);
        var failureCleanup = await cleanup.CleanupAsync(utcNow: DateTimeOffset.UtcNow.AddDays(31));
        failureCleanup.RemovedOperations.Should().Be(1);
        var parentCleanup = await cleanup.CleanupAsync(utcNow: DateTimeOffset.UtcNow.AddDays(31));
        parentCleanup.RemovedOperations.Should().Be(1);

        await VerifyPromptFanInWakeAfterContentionAsync(
            serviceProvider,
            persistence,
            fanOutCoordinator,
            registry,
            options,
            ownerId);
        await VerifyTaskResultRunnerSemanticsAsync(
            serviceProvider,
            persistence,
            fanOutCoordinator,
            registry,
            options,
            ownerId);
        await VerifyRunningCancellationAsync(
            serviceProvider,
            persistence,
            fanOutCoordinator,
            registry,
            options,
            ownerId);
    }

    private static async Task VerifyPromptFanInWakeAfterContentionAsync(
        ServiceProvider serviceProvider,
        NhBackgroundOperationPersistence persistence,
        NhBackgroundOperationFanOutCoordinator fanOutCoordinator,
        NhBackgroundOperationRegistry registry,
        NhBackgroundOperationsOptions options,
        Guid ownerId)
    {
        var parentId = Guid.NewGuid();
        await using (var seedScope = serviceProvider.CreateAsyncScope())
        {
            var context = seedScope.ServiceProvider.GetRequiredService<BackgroundOperationDbContext>();
            context.BackgroundOperations.Add(CreateQueuedOperation(
                parentId,
                ownerId,
                "provider-fan-in-parent"));
            await context.SaveChangesAsync();
        }

        var runner = new NhBackgroundOperationRunner(
            serviceProvider,
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            persistence,
            registry,
            fanOutCoordinator,
            options,
            NullLogger<NhBackgroundOperationRunner>.Instance);
        var dispatcher = new NhBackgroundOperationDispatchService(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            options,
            new NoOpLiveUpdatePublisher(),
            new NoOpNotificationProjector(),
            fanOutCoordinator,
            NullLogger<NhBackgroundOperationDispatchService>.Instance);

        await runner.RunAsync(parentId, 1);

        Guid[] childIds;
        await using (var suspendedScope = serviceProvider.CreateAsyncScope())
        {
            var context = suspendedScope.ServiceProvider.GetRequiredService<BackgroundOperationDbContext>();
            var parent = await context.BackgroundOperations
                .AsNoTracking()
                .SingleAsync(operation => operation.Id == parentId);
            parent.Status.Should().Be(NhBackgroundOperationStatus.WaitingForChildren);
            parent.NextDispatchAt.Should().BeAfter(DateTimeOffset.UtcNow.AddSeconds(10));
            childIds = await context.BackgroundOperations
                .AsNoTracking()
                .Where(operation => operation.ParentOperationId == parentId)
                .OrderBy(operation => operation.FanOutItemKey)
                .Select(operation => operation.Id)
                .ToArrayAsync();
            childIds.Should().HaveCount(2);
        }

        (await dispatcher.DispatchAvailableAsync(CancellationToken.None)).Should().Be(2);
        await runner.RunAsync(childIds[0], 1);

        DateTimeOffset promptRecheckAt;
        await using (var lockScope = serviceProvider.CreateAsyncScope())
        {
            var repository = lockScope.ServiceProvider.GetRequiredService<IRepository<NhBackgroundOperation>>();
            await using var transaction = await repository.StartOrGetTransactionScopeAsync();
            var acquired = await repository.TryAcquireTransactionLockAsync(
                transaction,
                $"NhBackgroundOperation:Operation:{parentId:N}",
                options.TransactionLockTimeoutMilliseconds);
            acquired.Should().BeTrue();

            await runner.RunAsync(childIds[1], 1);

            await using var contentionScope = serviceProvider.CreateAsyncScope();
            var context = contentionScope.ServiceProvider.GetRequiredService<BackgroundOperationDbContext>();
            var parent = await context.BackgroundOperations
                .AsNoTracking()
                .SingleAsync(operation => operation.Id == parentId);
            parent.Status.Should().Be(NhBackgroundOperationStatus.WaitingForChildren);
            parent.NextDispatchAt.Should().NotBeNull();
            promptRecheckAt = parent.NextDispatchAt!.Value;
            promptRecheckAt.Should().BeOnOrBefore(DateTimeOffset.UtcNow + options.DispatchInterval + TimeSpan.FromSeconds(1));
            (await context.BackgroundOperations
                    .AsNoTracking()
                    .Where(operation => operation.ParentOperationId == parentId)
                    .Select(operation => operation.Status)
                    .ToListAsync())
                .Should().OnlyContain(status => status == NhBackgroundOperationStatus.Succeeded);

            await transaction.CommitAsync();
        }

        var dispatchDelay = promptRecheckAt - DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(25);
        if (dispatchDelay > TimeSpan.Zero)
        {
            await Task.Delay(dispatchDelay);
        }

        (await dispatcher.DispatchAvailableAsync(CancellationToken.None)).Should().Be(1);
        await runner.RunAsync(parentId, 2);

        await using var verificationScope = serviceProvider.CreateAsyncScope();
        var verificationContext = verificationScope.ServiceProvider.GetRequiredService<BackgroundOperationDbContext>();
        var completedParent = await verificationContext.BackgroundOperations
            .AsNoTracking()
            .SingleAsync(operation => operation.Id == parentId);
        completedParent.Status.Should().Be(NhBackgroundOperationStatus.Succeeded);
        completedParent.ProgressPercentage.Should().Be(100);
        (await verificationContext.BackgroundOperationSteps
                .AsNoTracking()
                .SingleAsync(step => step.OperationId == parentId && step.StepKey == "publish-summary"))
            .Status.Should().Be(NhBackgroundOperationStepStatus.Succeeded);
        (await verificationContext.BackgroundOperationAttempts
                .AsNoTracking()
                .Where(attempt => attempt.OperationId == parentId)
                .OrderBy(attempt => attempt.AttemptNumber)
                .Select(attempt => attempt.Status)
                .ToListAsync())
            .Should().Equal(
                NhBackgroundOperationAttemptStatus.Suspended,
                NhBackgroundOperationAttemptStatus.Succeeded);
    }

    private static async Task VerifyRunningCancellationAsync(
        ServiceProvider serviceProvider,
        NhBackgroundOperationPersistence persistence,
        NhBackgroundOperationFanOutCoordinator fanOutCoordinator,
        NhBackgroundOperationRegistry registry,
        NhBackgroundOperationsOptions options,
        Guid ownerId)
    {
        var operationId = Guid.NewGuid();
        await using (var seedScope = serviceProvider.CreateAsyncScope())
        {
            var context = seedScope.ServiceProvider.GetRequiredService<BackgroundOperationDbContext>();
            context.BackgroundOperations.Add(CreateQueuedOperation(
                operationId,
                ownerId,
                "provider-cancellation"));
            await context.SaveChangesAsync();
        }

        var runner = new NhBackgroundOperationRunner(
            serviceProvider,
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            persistence,
            registry,
            fanOutCoordinator,
            options,
            NullLogger<NhBackgroundOperationRunner>.Instance);
        var runTask = runner.RunAsync(operationId, 1);

        var running = false;
        for (var attempt = 0; attempt < 100 && !running; attempt++)
        {
            await Task.Delay(20);
            await using var pollScope = serviceProvider.CreateAsyncScope();
            var context = pollScope.ServiceProvider.GetRequiredService<BackgroundOperationDbContext>();
            running = await context.BackgroundOperations
                .AsNoTracking()
                .AnyAsync(operation => operation.Id == operationId
                                       && operation.Status == NhBackgroundOperationStatus.Running);
        }

        running.Should().BeTrue();
        await using (var cancellationScope = serviceProvider.CreateAsyncScope())
        {
            var service = new NhBackgroundOperationService(
                cancellationScope.ServiceProvider.GetRequiredService<IRepository<NhBackgroundOperation>>(),
                registry,
                options,
                new NhHangfireQueueNameResolver(),
                new NoOpScheduler(),
                new NoOpLiveUpdatePublisher(),
                new NoOpNotificationProjector(),
                new Mapper(new MapperConfiguration(configuration =>
                    configuration.AddProfile<AutomapperProfileConfiguration>())),
                NullLogger<NhBackgroundOperationService>.Instance);
            (await service.RequestCancellationAsync(operationId, ownerId)).Success.Should().BeTrue();
        }

        await runTask.WaitAsync(TimeSpan.FromSeconds(10));
        await using var verificationScope = serviceProvider.CreateAsyncScope();
        var verificationContext = verificationScope.ServiceProvider.GetRequiredService<BackgroundOperationDbContext>();
        var operation = await verificationContext.BackgroundOperations
            .AsNoTracking()
            .SingleAsync(item => item.Id == operationId);
        operation.Status.Should().Be(NhBackgroundOperationStatus.Cancelled);
        operation.FailureCode.Should().Be("cancelled");
    }

    private static async Task VerifyTaskResultRunnerSemanticsAsync(
        ServiceProvider serviceProvider,
        NhBackgroundOperationPersistence persistence,
        NhBackgroundOperationFanOutCoordinator fanOutCoordinator,
        NhBackgroundOperationRegistry registry,
        NhBackgroundOperationsOptions options,
        Guid ownerId)
    {
        var expectedFailureId = Guid.NewGuid();
        var retryResultId = Guid.NewGuid();
        await using (var seedScope = serviceProvider.CreateAsyncScope())
        {
            var context = seedScope.ServiceProvider.GetRequiredService<BackgroundOperationDbContext>();
            context.BackgroundOperations.Add(CreateQueuedOperation(
                expectedFailureId,
                ownerId,
                "provider-expected-failure"));
            context.BackgroundOperations.Add(CreateQueuedOperation(
                retryResultId,
                ownerId,
                "provider-retry-result"));
            await context.SaveChangesAsync();
        }

        var runner = new NhBackgroundOperationRunner(
            serviceProvider,
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            persistence,
            registry,
            fanOutCoordinator,
            options,
            NullLogger<NhBackgroundOperationRunner>.Instance);

        await runner.RunAsync(expectedFailureId, 1);
        await runner.RunAsync(retryResultId, 1);

        await using var verificationScope = serviceProvider.CreateAsyncScope();
        var verificationContext = verificationScope.ServiceProvider.GetRequiredService<BackgroundOperationDbContext>();
        var expectedFailure = await verificationContext.BackgroundOperations
            .AsNoTracking()
            .SingleAsync(operation => operation.Id == expectedFailureId);
        expectedFailure.Status.Should().Be(NhBackgroundOperationStatus.Failed);
        expectedFailure.FailureCode.Should().Be("expected-business-outcome");
        expectedFailure.FailureMessageKey.Should().Be("background-operation.expected-business-outcome");
        expectedFailure.NextDispatchAt.Should().BeNull();

        var retryResult = await verificationContext.BackgroundOperations
            .AsNoTracking()
            .SingleAsync(operation => operation.Id == retryResultId);
        retryResult.Status.Should().Be(NhBackgroundOperationStatus.RetryScheduled);
        retryResult.FailureCode.Should().Be("known-transient-outcome");
        retryResult.FailureMessageKey.Should().Be("background-operation.known-transient-outcome");
        retryResult.NextDispatchAt.Should().NotBeNull();
    }

    private static NhBackgroundOperation CreateQueuedOperation(
        Guid operationId,
        Guid ownerId,
        string operationType)
    {
        var operation = new NhBackgroundOperation
        {
            Id = operationId,
            OperationType = operationType,
            OwnerUserId = ownerId,
            Status = NhBackgroundOperationStatus.Queued,
            DispatchGeneration = 1,
            Version = 1
        };
        operation.Steps.Add(new NhBackgroundOperationStep
        {
            Id = Guid.NewGuid(),
            OperationId = operationId,
            StepKey = "root",
            Status = NhBackgroundOperationStepStatus.Pending,
            AggregationMode = NhBackgroundOperationAggregationMode.Manual,
            Version = 1
        });
        return operation;
    }

    private static async Task VerifyHierarchyRetryAndCancellationAsync(
        ServiceProvider serviceProvider,
        NhBackgroundOperationsOptions options,
        NhBackgroundOperationRegistry registry,
        Guid parentId,
        Guid ownerId)
    {
        await using var serviceScope = serviceProvider.CreateAsyncScope();
        var service = new NhBackgroundOperationService(
            serviceScope.ServiceProvider.GetRequiredService<IRepository<NhBackgroundOperation>>(),
            registry,
            options,
            new NhHangfireQueueNameResolver(),
            new NoOpScheduler(),
            new NoOpLiveUpdatePublisher(),
            new NoOpNotificationProjector(),
            new Mapper(new MapperConfiguration(configuration =>
                configuration.AddProfile<AutomapperProfileConfiguration>())),
            NullLogger<NhBackgroundOperationService>.Instance);

        (await service.RetryAsync(parentId, ownerId)).Success.Should().BeTrue();
        await using (var retryVerificationScope = serviceProvider.CreateAsyncScope())
        {
            var context = retryVerificationScope.ServiceProvider.GetRequiredService<BackgroundOperationDbContext>();
            var hierarchy = await context.BackgroundOperations.AsNoTracking()
                .Where(x => x.Id == parentId || x.RootOperationId == parentId)
                .ToListAsync();
            hierarchy.Single(x => x.Id == parentId).Status.Should().Be(NhBackgroundOperationStatus.PendingDispatch);
            hierarchy.Single(x => x.FanOutKey == "provider-failing-child")
                .Status.Should().Be(NhBackgroundOperationStatus.PendingDispatch);
            hierarchy.Where(x => x.FanOutKey == "provider-children")
                .Should().OnlyContain(x => x.Status == NhBackgroundOperationStatus.Succeeded);
        }

        (await service.RequestCancellationAsync(parentId, ownerId)).Success.Should().BeTrue();
        await using var cancellationVerificationScope = serviceProvider.CreateAsyncScope();
        var cancellationContext = cancellationVerificationScope.ServiceProvider
            .GetRequiredService<BackgroundOperationDbContext>();
        var cancelledHierarchy = await cancellationContext.BackgroundOperations
            .Where(x => x.Id == parentId || x.RootOperationId == parentId)
            .ToListAsync();
        cancelledHierarchy.Single(x => x.Id == parentId).Status.Should().Be(NhBackgroundOperationStatus.CancelRequested);
        cancelledHierarchy.Single(x => x.FanOutKey == "provider-failing-child")
            .Status.Should().Be(NhBackgroundOperationStatus.CancelRequested);
        cancelledHierarchy.Where(x => x.FanOutKey == "provider-children")
            .Should().OnlyContain(x => x.Status == NhBackgroundOperationStatus.Succeeded);

        var completedAt = DateTimeOffset.UtcNow;
        foreach (var target in cancelledHierarchy.Where(x => x.Status == NhBackgroundOperationStatus.CancelRequested))
        {
            target.Status = NhBackgroundOperationStatus.Failed;
            target.CompletedAt = completedAt;
        }
        await cancellationContext.SaveChangesAsync();
    }

    private sealed class NoOpLiveUpdatePublisher : INhBackgroundOperationLiveUpdatePublisher
    {
        public Task PublishChangedAsync(
            Guid ownerUserId,
            NhBackgroundOperationChangedMessage message,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed record ProviderChildRequest(string Value);

    private sealed record ProviderExpectedFailureRequest;

    private sealed record ProviderRetryResultRequest;

    private sealed record ProviderCancellationRequest;

    private sealed record ProviderFanInParentRequest;

    private sealed record ProviderFanInChildRequest(int Index);

    private sealed record ProviderParentRequest;

    private sealed class ProviderParentHandler : INhBackgroundOperationHandler<ProviderParentRequest>
    {
        public Task<TaskResult> ExecuteAsync(
            ProviderParentRequest request,
            INhBackgroundOperationContext context,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(TaskResult.Succeeded());
        }
    }

    private sealed class ProviderChildHandler : INhBackgroundOperationHandler<ProviderChildRequest>
    {
        public Task<TaskResult> ExecuteAsync(
            ProviderChildRequest request,
            INhBackgroundOperationContext context,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(TaskResult.Succeeded());
        }
    }

    private sealed class ProviderFanInParentHandler :
        INhBackgroundOperationHandler<ProviderFanInParentRequest>
    {
        public async Task<TaskResult> ExecuteAsync(
            ProviderFanInParentRequest request,
            INhBackgroundOperationContext context,
            CancellationToken cancellationToken)
        {
            await context.Progress.DefineAsync(plan => plan
                .Step("fan-out", 90, "provider.fan-out")
                .Step("publish-summary", 10, "provider.publish-summary"),
                cancellationToken);

            var fanOutResult = await context.FanOut.RunAsync(
                "fan-out",
                Enumerable.Range(1, 2).Select(index => NhBackgroundOperationFanOut.Item(
                    $"child-{index}",
                    new ProviderFanInChildRequest(index))),
                cancellationToken);
            if (!fanOutResult.Success)
            {
                return fanOutResult;
            }

            return await context.Progress.RunStepAsync(
                "publish-summary",
                async (step, token) =>
                {
                    await step.ReportAsync(1, 1, cancellationToken: token);
                    return TaskResult.Succeeded();
                },
                cancellationToken);
        }
    }

    private sealed class ProviderFanInChildHandler :
        INhBackgroundOperationHandler<ProviderFanInChildRequest>
    {
        public Task<TaskResult> ExecuteAsync(
            ProviderFanInChildRequest request,
            INhBackgroundOperationContext context,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(TaskResult.Succeeded());
        }
    }

    private sealed class ProviderExpectedFailureHandler : INhBackgroundOperationHandler<ProviderExpectedFailureRequest>
    {
        public Task<TaskResult> ExecuteAsync(
            ProviderExpectedFailureRequest request,
            INhBackgroundOperationContext context,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(TaskResult.Failed(
                "expected-business-outcome",
                "background-operation.expected-business-outcome"));
        }
    }

    private sealed class ProviderRetryResultHandler : INhBackgroundOperationHandler<ProviderRetryResultRequest>
    {
        public Task<TaskResult> ExecuteAsync(
            ProviderRetryResultRequest request,
            INhBackgroundOperationContext context,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<TaskResult>(NhBackgroundOperationRetryResult.Retry(
                "known-transient-outcome",
                "background-operation.known-transient-outcome",
                TimeSpan.FromMilliseconds(10)));
        }
    }

    private sealed class ProviderCancellationHandler : INhBackgroundOperationHandler<ProviderCancellationRequest>
    {
        public async Task<TaskResult> ExecuteAsync(
            ProviderCancellationRequest request,
            INhBackgroundOperationContext context,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return TaskResult.Succeeded();
        }
    }

    private sealed class NoOpNotificationProjector : INhBackgroundOperationNotificationProjector
    {
        public Task<TaskResult> ProjectAsync(
            Guid operationId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(TaskResult.Succeeded());
        }
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

        public Task<bool> DeleteAsync(
            string schedulerJobId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        public Task<NhBackgroundOperationExecutionState?> GetStateAsync(
            string schedulerJobId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<NhBackgroundOperationExecutionState?>(null);
        }
    }

    private sealed class BackgroundOperationDbContext(DbContextOptions<BackgroundOperationDbContext> options)
        : NhIdentityDbContext(options);
}