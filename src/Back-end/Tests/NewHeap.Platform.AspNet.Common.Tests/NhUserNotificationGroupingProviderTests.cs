using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models.Mutate;
using NewHeap.Platform.AspNet.Common.PostgreSql;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NewHeap.Platform.AspNet.Common.Services.Notification;
using NewHeap.Platform.AspNet.Common.SqlServer;
using NewHeap.Platform.AspNet.Common.Utilities;
using NewHeap.Platform.Common.Services;
using NewHeap.Platform.Common.Translations;
using NewHeap.Platform.Mapping;
using NSubstitute;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

public sealed class NhUserNotificationGroupingProviderTests
{
    [Fact]
    public async Task GroupedNotificationsAndPolicyProjectionWorkOnBothRelationalProviders()
    {
        await using (var sqlServer = new MsSqlBuilder(
            "mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04").Build())
        {
            await sqlServer.StartAsync();
            await VerifyProviderAsync(
                options => options.UseNewHeapSqlServer(sqlServer.GetConnectionString()),
                "sql-server");
        }

        await using (var postgreSql = new PostgreSqlBuilder("postgres:15.1").Build())
        {
            await postgreSql.StartAsync();
            await VerifyProviderAsync(
                options => options.UseNewHeapPostgreSql(postgreSql.GetConnectionString()),
                "postgresql");
        }
    }

    private static async Task VerifyProviderAsync(
        Action<DbContextOptionsBuilder> configureProvider,
        string providerName)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<GroupingDbContext>(options =>
        {
            configureProvider(options);
            options.ConfigureWarnings(warnings => warnings.Throw(
                CoreEventId.FirstWithoutOrderByAndFilterWarning,
                CoreEventId.RowLimitingOperationWithoutOrderByWarning));
        });
        services.AddScoped<IRepository<NhUserNotification>>(serviceProvider =>
            new Repository<NhUserNotification>(
                serviceProvider.GetRequiredService<GroupingDbContext>(),
                serviceProvider));
        services.AddScoped<IRepository<NhBackgroundOperation>>(serviceProvider =>
            new Repository<NhBackgroundOperation>(
                serviceProvider.GetRequiredService<GroupingDbContext>(),
                serviceProvider));
        services.AddScoped<INhUserNotificationService>(CreateNotificationService);
        services.AddScoped<INhBackgroundOperationNotificationFormatter, NhDefaultBackgroundOperationNotificationFormatter>();
        services.AddScoped<NhDefaultBackgroundOperationNotificationPolicy>();
        services.AddScoped<INhBackgroundOperationNotificationPolicy, GroupByDomainObjectPolicy>();
        await using var serviceProvider = services.BuildServiceProvider();

        var userId = Guid.NewGuid();
        await using (var seedScope = serviceProvider.CreateAsyncScope())
        {
            var context = seedScope.ServiceProvider.GetRequiredService<GroupingDbContext>();
            await context.Database.EnsureCreatedAsync();
            context.Users.Add(new NhUser
            {
                Id = userId,
                UserName = $"{providerName}-grouping@example.test",
                NormalizedUserName = $"{providerName.ToUpperInvariant()}-GROUPING@EXAMPLE.TEST"
            });
            await context.SaveChangesAsync();
        }

        await VerifyGroupedServiceAsync(serviceProvider, userId);
        await VerifyPolicyProjectionAsync(serviceProvider, userId);
    }

    private static async Task VerifyGroupedServiceAsync(ServiceProvider serviceProvider, Guid userId)
    {
        Guid firstThreadId;
        await using (var scope = serviceProvider.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<INhUserNotificationService>();

            var created = await service.CreateOrAddMessageAsync(CreateModel(
                userId,
                "Report queued for review",
                NhUserNotificationSeverity.Information,
                "/reports/1"));
            created.Success.Should().BeTrue();
            firstThreadId = created.Data!.Id;
            (await service.MarkIsLastReadAsync(firstThreadId, true)).Success.Should().BeTrue();

            var appended = await service.CreateOrAddMessageAsync(CreateModel(
                userId,
                "Report failed",
                NhUserNotificationSeverity.Error,
                "/reports/1/errors"));
            appended.Success.Should().BeTrue();
            appended.Data!.Id.Should().Be(firstThreadId);

            var otherThread = await service.CreateOrAddMessageAsync(CreateModel(
                userId,
                "Other report ready",
                NhUserNotificationSeverity.Success,
                "/reports/2",
                "report:2"));
            otherThread.Data!.Id.Should().NotBe(firstThreadId);
        }

        await using (var verificationScope = serviceProvider.CreateAsyncScope())
        {
            var context = verificationScope.ServiceProvider.GetRequiredService<GroupingDbContext>();
            var thread = await context.UserNotifications
                .AsNoTracking()
                .Include(x => x.Messages)
                .SingleAsync(x => x.Id == firstThreadId);
            thread.Messages.Should().HaveCount(2);
            thread.Severity.Should().Be(NhUserNotificationSeverity.Error);
            thread.LastTitle.Should().Be("Report failed");
            thread.IsLastRead.Should().BeFalse();
            thread.Category.Should().Be("report");
            thread.GroupKey.Should().Be("report:1");
            thread.Data.Url.Should().Be("/reports/1/errors");
            thread.Messages.Select(x => x.Severity).Should().BeEquivalentTo(
                [NhUserNotificationSeverity.Information, NhUserNotificationSeverity.Error]);
        }

        await using (var archiveScope = serviceProvider.CreateAsyncScope())
        {
            var service = archiveScope.ServiceProvider.GetRequiredService<INhUserNotificationService>();
            (await service.ArchiveAsync(firstThreadId, true)).Success.Should().BeTrue();

            var overview = await service.GetOverviewByUserIdAsync(userId);
            overview.TotalCount.Should().Be(1);
            overview.UnreadCount.Should().Be(1);

            var restarted = await service.CreateOrAddMessageAsync(CreateModel(
                userId,
                "Report queued again",
                NhUserNotificationSeverity.Information,
                "/reports/1"));
            restarted.Data!.Id.Should().NotBe(firstThreadId);
            (await service.GetActiveByGroupKeyAsync(userId, " report:1 "))!.Id.Should().Be(restarted.Data.Id);
        }

        await using var cleanupScope = serviceProvider.CreateAsyncScope();
        var cleanupContext = cleanupScope.ServiceProvider.GetRequiredService<GroupingDbContext>();
        cleanupContext.UserNotifications.RemoveRange(
            await cleanupContext.UserNotifications.Where(x => x.UserId == userId).ToListAsync());
        await cleanupContext.SaveChangesAsync();
    }

    private static async Task VerifyPolicyProjectionAsync(ServiceProvider serviceProvider, Guid userId)
    {
        var firstOperationId = Guid.NewGuid();
        var secondOperationId = Guid.NewGuid();
        await using (var seedScope = serviceProvider.CreateAsyncScope())
        {
            var context = seedScope.ServiceProvider.GetRequiredService<GroupingDbContext>();
            context.BackgroundOperations.AddRange(
                CreateCompletedOperation(firstOperationId, userId),
                CreateCompletedOperation(secondOperationId, userId));
            await context.SaveChangesAsync();
        }

        var projector = new NhBackgroundOperationNotificationProjector(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            new NhBackgroundOperationsOptions { TransactionLockTimeoutMilliseconds = 1_000 });

        (await projector.ProjectAsync(firstOperationId)).Success.Should().BeTrue();
        (await projector.ProjectAsync(secondOperationId)).Success.Should().BeTrue();
        (await projector.ProjectAsync(secondOperationId)).Success.Should().BeTrue();

        await using var verificationScope = serviceProvider.CreateAsyncScope();
        var verificationContext = verificationScope.ServiceProvider.GetRequiredService<GroupingDbContext>();
        var operations = await verificationContext.BackgroundOperations
            .AsNoTracking()
            .Where(x => x.Id == firstOperationId || x.Id == secondOperationId)
            .ToListAsync();
        operations.Should().OnlyContain(x => x.LastProjectedNotificationEventSequence == 3);
        operations.Select(x => x.UserNotificationId).Distinct().Should().ContainSingle();

        var notification = await verificationContext.UserNotifications
            .AsNoTracking()
            .Include(x => x.Messages)
            .SingleAsync(x => x.Id == operations[0].UserNotificationId);
        notification.GroupKey.Should().Be("project:7");
        notification.Category.Should().Be(NhBackgroundOperationNotificationCategories.BackgroundOperation);
        notification.Severity.Should().Be(NhUserNotificationSeverity.Success);
        notification.Data.Url.Should().Be("/projects/7");
        notification.Messages.Should().HaveCount(2)
            .And.OnlyContain(x => x.Message == "The operation completed successfully.");
    }

    private static NhUserNotificationMutateModel CreateModel(
        Guid userId,
        string title,
        NhUserNotificationSeverity severity,
        string url,
        string groupKey = "report:1")
    {
        return new NhUserNotificationMutateModel
        {
            UserId = userId,
            Title = title,
            Message = title,
            Url = url,
            Category = "report",
            Severity = severity,
            GroupKey = groupKey
        };
    }

    private static NhBackgroundOperation CreateCompletedOperation(Guid operationId, Guid ownerId)
    {
        var operation = new NhBackgroundOperation
        {
            Id = operationId,
            OperationType = "provider-grouping-test",
            PayloadJson = "{}",
            OwnerUserId = ownerId,
            DomainObjectType = "project",
            DomainObjectId = "7",
            Status = NhBackgroundOperationStatus.Succeeded,
            CompletedAt = DateTimeOffset.UtcNow,
            Version = 1
        };
        NhBackgroundOperationService.AppendEvent(
            operation,
            NhBackgroundOperationEventType.StateChanged,
            NhBackgroundOperationMessageSeverity.Information,
            "background-operation.started",
            null,
            true);
        NhBackgroundOperationService.AppendEvent(
            operation,
            NhBackgroundOperationEventType.ResultAvailable,
            NhBackgroundOperationMessageSeverity.Success,
            "background-operation.result-available",
            null,
            true);
        NhBackgroundOperationService.AppendEvent(
            operation,
            NhBackgroundOperationEventType.StateChanged,
            NhBackgroundOperationMessageSeverity.Success,
            "background-operation.succeeded",
            null,
            true);
        return operation;
    }

    private static NhUserNotificationService CreateNotificationService(IServiceProvider serviceProvider)
    {
        var logHelper = new LogHelperService(
            Substitute.For<IStringLocalizer<SharedDataAnnotationRecources>>(),
            NullLogger<LogHelperService>.Instance);

        return new NhUserNotificationService(
            serviceProvider.GetRequiredService<IRepository<NhUserNotification>>(),
            Substitute.For<IStringLocalizer<NhUserNotificationService>>(),
            Substitute.For<INhDbLogService>(),
            logHelper,
            new ValidationService(serviceProvider),
            new Mapper(new MapperConfiguration(configuration =>
                configuration.AddProfile<AutomapperProfileConfiguration>())),
            NullLogger<NhNotificationService>.Instance);
    }

    private sealed class GroupByDomainObjectPolicy(NhDefaultBackgroundOperationNotificationPolicy defaultPolicy)
        : INhBackgroundOperationNotificationPolicy
    {
        public async Task<NhBackgroundOperationNotificationDecision> DecideAsync(
            NhBackgroundOperation operation,
            NhBackgroundOperationEvent milestone,
            CancellationToken cancellationToken = default)
        {
            var decision = await defaultPolicy.DecideAsync(operation, milestone, cancellationToken);
            if (!decision.ShouldNotify)
            {
                return decision;
            }

            return decision with
            {
                GroupKey = $"{operation.DomainObjectType}:{operation.DomainObjectId}",
                Url = $"/{operation.DomainObjectType}s/{operation.DomainObjectId}"
            };
        }
    }

    private sealed class GroupingDbContext(DbContextOptions<GroupingDbContext> options)
        : NhIdentityDbContext(options);
}
