using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NewHeap.Platform.AspNet.Common.Services.Notification;
using NewHeap.Platform.Common.Services;
using NewHeap.Platform.Common.Translations;
using NewHeap.Platform.Mapping;
using NSubstitute;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

public sealed class NhEfWarningCompatibilityProviderTests
{
    [Fact]
    public async Task NotificationOverviewAndBackgroundStartupAcceptStrictEfWarningsOnBothRelationalProviders()
    {
        await using (var sqlServer = new MsSqlBuilder(
            "mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04").Build())
        {
            await sqlServer.StartAsync();
            await VerifyProviderAsync(
                options => options.UseSqlServer(sqlServer.GetConnectionString()),
                "sql-server");
        }

        await using (var postgreSql = new PostgreSqlBuilder("postgres:15.1").Build())
        {
            await postgreSql.StartAsync();
            await VerifyProviderAsync(
                options => options.UseNpgsql(postgreSql.GetConnectionString()),
                "postgresql");
        }
    }

    private static async Task VerifyProviderAsync(
        Action<DbContextOptionsBuilder> configureProvider,
        string providerName)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<WarningCompatibilityDbContext>(options =>
        {
            configureProvider(options);
            options.ConfigureWarnings(warnings => warnings.Throw(
                CoreEventId.FirstWithoutOrderByAndFilterWarning,
                CoreEventId.RowLimitingOperationWithoutOrderByWarning));
        });
        services.AddScoped<IRepository<NhUserNotification>>(serviceProvider =>
            new Repository<NhUserNotification>(
                serviceProvider.GetRequiredService<WarningCompatibilityDbContext>(),
                serviceProvider));
        services.AddScoped<IRepository<NhBackgroundOperation>>(serviceProvider =>
            new Repository<NhBackgroundOperation>(
                serviceProvider.GetRequiredService<WarningCompatibilityDbContext>(),
                serviceProvider));
        await using var serviceProvider = services.BuildServiceProvider();

        var userId = Guid.NewGuid();
        var latestNotificationDate = DateTimeOffset.UtcNow.AddMinutes(-1);
        await using (var seedScope = serviceProvider.CreateAsyncScope())
        {
            var context = seedScope.ServiceProvider.GetRequiredService<WarningCompatibilityDbContext>();
            await context.Database.EnsureCreatedAsync();
            context.Users.Add(new NhUser
            {
                Id = userId,
                UserName = $"{providerName}@example.test",
                NormalizedUserName = $"{providerName.ToUpperInvariant()}@EXAMPLE.TEST"
            });
            context.UserNotifications.AddRange(
                new NhUserNotification
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    LastTitle = "Earlier notification",
                    LastMessage = "Earlier notification message",
                    IsLastRead = true,
                    LastModifiedDateTime = latestNotificationDate.AddMinutes(-1)
                },
                new NhUserNotification
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    LastTitle = "Latest notification",
                    LastMessage = "Latest notification message",
                    IsLastRead = false,
                    LastModifiedDateTime = latestNotificationDate
                });
            await context.SaveChangesAsync();
        }

        await using (var overviewScope = serviceProvider.CreateAsyncScope())
        {
            var notificationService = CreateNotificationService(overviewScope.ServiceProvider);

            var overview = await notificationService.GetOverviewByUserIdAsync(userId);

            overview.TotalCount.Should().Be(2);
            overview.UnreadCount.Should().Be(1);
            overview.LastNotificationDate.Should().BeCloseTo(
                latestNotificationDate,
                TimeSpan.FromMilliseconds(1));

            var emptyOverview = await notificationService.GetOverviewByUserIdAsync(Guid.NewGuid());
            emptyOverview.TotalCount.Should().Be(0);
            emptyOverview.UnreadCount.Should().Be(0);
            emptyOverview.LastNotificationDate.Should().BeNull();
        }

        var backgroundOperationOptions = new NhBackgroundOperationsOptions
        {
            DispatchWorkersEnabled = false,
            ReconciliationEnabled = false,
            CleanupEnabled = false,
            LiveUpdatesEnabled = false,
            UserNotificationProjectionEnabled = false
        };
        var startupValidator = new NhBackgroundOperationStartupValidator(
            serviceProvider,
            backgroundOperationOptions,
            NullLogger<NhBackgroundOperationStartupValidator>.Instance);

        await startupValidator.StartAsync(CancellationToken.None);
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
            Substitute.For<IMapper>(),
            NullLogger<NhNotificationService>.Instance);
    }

    private sealed class WarningCompatibilityDbContext(
        DbContextOptions<WarningCompatibilityDbContext> options)
        : NhIdentityDbContext(options);
}
