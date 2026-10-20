using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NewHeap.Platform.AspNet.Common.Controllers;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models.View;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NewHeap.Platform.Common.Actors;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Tests.Relational;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

/// <summary>
/// Background operations and notifications hosted in an application's own EF context, without
/// NewHeap Identity, on both relational providers.
/// </summary>
public sealed class NhPlatformModuleProviderTests
{
    private const string OperationType = "portal-report";
    private const string NotificationSchema = "portal_notify";

    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task ModulesRunInAPlainContextOnEachRelationalProvider(RelationalTestProvider provider)
    {
        var database = await ProviderTestDatabase.CreateAsync(provider);
        await VerifyProviderAsync(database.UseNewHeapProvider, database.Label);
    }

    private static async Task VerifyProviderAsync(Action<DbContextOptionsBuilder> configureProvider, string providerName)
    {
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        await using var serviceProvider = CreateServices(configureProvider, notifications: true, tenantA, alice);

        await using (var scope = serviceProvider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<PortalDbContext>();
            await context.Database.EnsureCreatedAsync();
            context.Users.AddRange(new PortalUser(alice, "Alice"), new PortalUser(bob, "Bob"));
            await context.SaveChangesAsync();

            (await TableSchemaAsync(context, "BackgroundOperations")).Should().Be(
                NhPlatformModuleModelBuilderExtensions.BackgroundOperationsDefaultSchema, providerName);
            (await TableSchemaAsync(context, "UserNotifications")).Should().Be(NotificationSchema, providerName);
        }

        // The platform's startup checks accept the plain context with the notifications module.
        foreach (var validator in serviceProvider.GetServices<IHostedService>().OfType<NhBackgroundOperationStartupValidator>())
        {
            await validator.StartAsync(CancellationToken.None);
        }

        var committed = await EnqueueInTransactionAsync(serviceProvider, alice, tenantA, "committed", commit: true);
        var rolledBack = await EnqueueInTransactionAsync(serviceProvider, alice, tenantA, "rolled-back", commit: false);
        await using (var scope = serviceProvider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<PortalDbContext>();
            (await context.Set<NhBackgroundOperation>().AnyAsync(operation => operation.Id == committed)).Should().BeTrue(providerName);
            (await context.Set<NhBackgroundOperation>().AnyAsync(operation => operation.Id == rolledBack)).Should().BeFalse(providerName);
            (await context.Orders.Select(order => order.Reference).ToListAsync()).Should().Equal(["committed"]);
        }

        // A milestone of Alice's operation reaches her through the notifications module, twice in one thread.
        await PublishMilestoneAsync(serviceProvider, committed);
        await PublishMilestoneAsync(serviceProvider, committed);
        await using (var scope = serviceProvider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<PortalDbContext>();
            var notification = await context.Set<NhUserNotification>()
                .Include(item => item.Messages)
                .SingleAsync(item => item.UserId == alice);
            notification.Category.Should().Be(NhBackgroundOperationNotificationCategories.BackgroundOperation);
            notification.Messages.Should().HaveCount(2, providerName);
            (await context.Set<NhBackgroundOperation>().SingleAsync(operation => operation.Id == committed))
                .UserNotificationId.Should().Be(notification.Id);
        }

        // The controller asks the application who the user is and which tenant they may use.
        await using (var scope = serviceProvider.CreateAsyncScope())
        {
            var listed = await ListAsync(scope.ServiceProvider);
            listed.Should().BeOfType<OkObjectResult>()
                .Which.Value.Should().BeOfType<CollectionResultModel<NhBackgroundOperationViewModel>>()
                .Which.Items.Select(item => item.Id).Should().Equal([committed]);
            scope.ServiceProvider.GetRequiredService<PortalSession>().Use(alice, tenantB);
            (await ListAsync(scope.ServiceProvider)).Should().BeOfType<ForbidResult>();
        }

        // Without foreign keys, retention removes what the cascades remove in identity hosting.
        var child = await AddChildAsync(serviceProvider, committed);
        var bobs = await EnqueueInTransactionAsync(serviceProvider, bob, tenantB, "bob", commit: true);
        await using (var scope = serviceProvider.CreateAsyncScope())
        {
            var retention = scope.ServiceProvider.GetRequiredService<INhRetention>();
            (await retention.ForgetUserAsync(alice)).Success.Should().BeTrue(providerName);
            (await retention.ForgetUserAsync(alice)).Success.Should().BeTrue(providerName);
            var context = scope.ServiceProvider.GetRequiredService<PortalDbContext>();
            (await context.Set<NhBackgroundOperation>().Select(operation => operation.Id).ToListAsync())
                .Should().Equal([bobs]);
            (await context.Set<NhBackgroundOperation>().AnyAsync(operation => operation.Id == child)).Should().BeFalse();
            (await context.Set<NhUserNotification>().AnyAsync(item => item.UserId == alice)).Should().BeFalse();

            (await retention.ForgetDivisionAsync(tenantB)).Success.Should().BeTrue(providerName);
            (await context.Set<NhBackgroundOperation>().AnyAsync()).Should().BeFalse(providerName);
        }

        // Without a notification store, enabled projection is a configuration error.
        await using var withoutNotifications = CreateServices(configureProvider, notifications: false, tenantA, alice);
        var startup = async () =>
        {
            foreach (var validator in withoutNotifications.GetServices<IHostedService>().OfType<NhBackgroundOperationStartupValidator>())
            {
                await validator.StartAsync(CancellationToken.None);
            }
        };
        await startup.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*INhUserNotificationSink*");
    }

    private static ServiceProvider CreateServices(
        Action<DbContextOptionsBuilder> configureProvider,
        bool notifications,
        Guid tenant,
        Guid user)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddDbContext<PortalDbContext>(configureProvider);
        services.AddScoped(_ => new PortalSession(user, tenant));
        services.AddSingleton<INhBackgroundOperationScheduler, NoOpScheduler>();
        if (notifications)
        {
            services.AddNewHeapNotifications<PortalDbContext>();
        }

        services.AddNewHeapBackgroundOperations<PortalDbContext>(operations =>
        {
            operations.Options.DispatchWorkersEnabled = false;
            operations.Options.LiveUpdatesEnabled = false;
            operations.Options.ReconciliationEnabled = false;
            operations.Options.CleanupEnabled = false;
            operations.Add<PortalReportRequest, PortalReportHandler>(OperationType);
        });
        services.AddNewHeapActorContext<PortalActorContext>();
        services.AddNewHeapDivisionAccess<PortalTenantAccess>();
        services.AddNewHeapActorDirectory<PortalActorDirectory>();
        return services.BuildServiceProvider();
    }

    private static async Task<Guid> EnqueueInTransactionAsync(
        ServiceProvider serviceProvider,
        Guid owner,
        Guid tenant,
        string reference,
        bool commit)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<PortalDbContext>();
        var operations = scope.ServiceProvider.GetRequiredService<INhBackgroundOperationService>();
        await using var transaction = await context.Database.BeginTransactionAsync();

        context.Orders.Add(new PortalOrder(Guid.NewGuid(), reference));
        await context.SaveChangesAsync();
        var enqueued = await operations.EnqueueAsync(
            new PortalReportRequest(reference),
            new NhBackgroundOperationEnqueueOptions { OwnerUserId = owner, DivisionId = tenant });
        enqueued.Success.Should().BeTrue();

        if (commit)
        {
            await transaction.CommitAsync();
        }
        else
        {
            await transaction.RollbackAsync();
        }

        return enqueued.Data!.Id;
    }

    private static async Task PublishMilestoneAsync(ServiceProvider serviceProvider, Guid operationId)
    {
        await using (var scope = serviceProvider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<PortalDbContext>();
            var operation = await context.Set<NhBackgroundOperation>().SingleAsync(item => item.Id == operationId);
            operation.LatestEventSequence++;
            context.Set<NhBackgroundOperationEvent>().Add(new NhBackgroundOperationEvent
            {
                Id = Guid.NewGuid(),
                OperationId = operationId,
                Sequence = operation.LatestEventSequence,
                EventType = NhBackgroundOperationEventType.Message,
                Severity = NhBackgroundOperationMessageSeverity.Success,
                MessageKey = "portal.report-ready",
                IsMilestone = true
            });
            await context.SaveChangesAsync();
        }

        var projector = serviceProvider.GetRequiredService<INhBackgroundOperationNotificationProjector>();
        (await projector.ProjectAsync(operationId)).Success.Should().BeTrue();
    }

    private static async Task<Guid> AddChildAsync(ServiceProvider serviceProvider, Guid parentId)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<PortalDbContext>();
        var child = await context.Set<NhBackgroundOperation>().AsNoTracking().SingleAsync(item => item.Id == parentId);
        child.Id = Guid.NewGuid();
        child.ParentOperationId = parentId;
        child.RootOperationId = parentId;
        child.FanOutKey = "items";
        child.FanOutItemKey = "1";
        child.UserNotificationId = null;
        context.Set<NhBackgroundOperation>().Add(child);
        await context.SaveChangesAsync();
        return child.Id;
    }

    private static Task<IActionResult> ListAsync(IServiceProvider services)
    {
        var httpContext = new DefaultHttpContext { RequestServices = services };
        services.GetRequiredService<IHttpContextAccessor>().HttpContext = httpContext;
        var controller = ActivatorUtilities.CreateInstance<NhBackgroundOperationController>(services);
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller.Get(new NhBackgroundOperationCollectionRequestModel());
    }

    private static async Task<string?> TableSchemaAsync(PortalDbContext context, string table)
    {
        return await context.Database
            .SqlQueryRaw<string>(
                "SELECT table_schema AS \"Value\" FROM information_schema.tables WHERE table_name = {0}",
                table)
            .SingleOrDefaultAsync();
    }

    private sealed record PortalReportRequest(string Reference);

    private sealed class PortalReportHandler : INhBackgroundOperationHandler<PortalReportRequest>
    {
        public Task<TaskResult> ExecuteAsync(
            PortalReportRequest request,
            INhBackgroundOperationContext context,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(TaskResult.Succeeded());
        }
    }

    /// <summary>The signed-in partner and the tenant they work in, as the application tracks them.</summary>
    private sealed class PortalSession(Guid user, Guid tenant)
    {
        public Guid User { get; private set; } = user;

        public Guid Tenant { get; private set; } = tenant;

        public Guid AllowedTenant { get; } = tenant;

        public void Use(Guid user, Guid tenant)
        {
            User = user;
            Tenant = tenant;
        }
    }

    private sealed class PortalActorContext(PortalSession session) : INhActorContextAccessor
    {
        public ValueTask<NhActorContext> GetCurrentAsync(CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(new NhActorContext(NhActorRef.ForUser(session.User), session.Tenant));
        }
    }

    private sealed class PortalTenantAccess(PortalSession session) : INhDivisionAccess
    {
        public ValueTask<bool> HasAccessAsync(NhActorRef actor, Guid divisionId, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(divisionId == session.AllowedTenant);
        }
    }

    private sealed class PortalActorDirectory(PortalDbContext context) : INhActorDirectory
    {
        public async Task<IReadOnlyDictionary<NhActorRef, NhActorInfo>> GetAsync(
            IReadOnlyCollection<NhActorRef> actors,
            CancellationToken cancellationToken = default)
        {
            var ids = actors.Select(actor => actor.TryGetUserId(out var id) ? id : Guid.Empty).ToList();
            var users = await context.Users.Where(user => ids.Contains(user.Id)).ToListAsync(cancellationToken);
            return users.ToDictionary(
                user => NhActorRef.ForUser(user.Id),
                user => new NhActorInfo(NhActorRef.ForUser(user.Id), user.Name));
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
            return Task.FromResult(new NhBackgroundOperationScheduleResult($"job-{operationId:N}-{dispatchGeneration}"));
        }

        public Task<bool> DeleteAsync(string schedulerJobId, CancellationToken cancellationToken = default)
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

    private sealed record PortalUser(Guid Id, string Name);

    private sealed record PortalOrder(Guid Id, string Reference);

    private sealed class PortalDbContext(DbContextOptions<PortalDbContext> options) : DbContext(options)
    {
        public DbSet<PortalUser> Users => Set<PortalUser>();

        public DbSet<PortalOrder> Orders => Set<PortalOrder>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<PortalUser>().ToTable("PortalUsers");
            modelBuilder.Entity<PortalOrder>().ToTable("PortalOrders");
            modelBuilder.AddNewHeapBackgroundOperations();
            modelBuilder.AddNewHeapNotifications(notifications => notifications.Schema = NotificationSchema);
        }
    }
}
