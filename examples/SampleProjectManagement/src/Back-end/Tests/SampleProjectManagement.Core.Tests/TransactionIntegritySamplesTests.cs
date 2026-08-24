using NewHeap.Platform.Mapping;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models.Options;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Events;
using NewHeap.Platform.Common.Services;
using NewHeap.Platform.Common.Translations;
using NSubstitute;
using SampleProjectManagement.Api.Events;
using SampleProjectManagement.Core.Events;
using SampleProjectManagement.Core.Models.Mutate;
using SampleProjectManagement.Core.Services;
using SampleProjectManagement.DAL;
using SampleProjectManagement.DAL.Entities;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// SPM-195–200: executable transaction-integrity cases around the concrete
/// project application service. The service owns the unit of work; nested
/// library calls may join it but cannot commit it.
/// </summary>
public sealed class TransactionIntegritySamplesTests
{
    [Fact]
    public async Task PublishFailureRollsBackAndNeverCommits()
    {
        await using var dbContext = CreateDbContext();
        using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var steps = new List<string>();
        var transaction = CreateTransaction(steps);
        var publisher = Substitute.For<INhEventPublisher>();
        publisher.PublishAsync(Arg.Any<ProjectCreatedEvent>())
            .Returns(_ =>
            {
                steps.Add("publish-failed");
                throw new InvalidOperationException("Broker sample failure");
            });
        var service = CreateService(dbContext, serviceProvider, transaction, publisher, steps);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(
            NewProject("ROLLBACK-PUBLISH"),
            options: new BaseDbEntityServiceOperationOptions { DbLoggingDisabled = true }));

        Assert.Equal(["save", "publish-failed", "rollback"], steps);
        await transaction.Received(1).RollbackAsync(Arg.Any<CancellationToken>());
        await transaction.DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeliberateRollbackPublishesBeforeRollbackAndNeverCommits()
    {
        await using var dbContext = CreateDbContext();
        using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var steps = new List<string>();
        var transaction = CreateTransaction(steps);
        var publisher = Substitute.For<INhEventPublisher>();
        publisher.PublishAsync(Arg.Any<ProjectCreatedEvent>())
            .Returns(_ =>
            {
                steps.Add("publish");
                return Task.CompletedTask;
            });
        var service = CreateService(dbContext, serviceProvider, transaction, publisher, steps);

        var result = await service.CreateRolledBackSampleAsync(NewProject("ROLLBACK-SAMPLE"));

        Assert.True(result.Success);
        Assert.NotEqual(Guid.Empty, result.Data?.ProjectId);
        Assert.NotEqual(Guid.Empty, result.Data?.EventId);
        Assert.Equal(["save", "publish", "rollback"], steps);
        await transaction.Received(1).RollbackAsync(Arg.Any<CancellationToken>());
        await transaction.DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NestedScopeCannotCommitTheOuterTransaction()
    {
        var databaseTransaction = Substitute.For<ITransaction>();
        databaseTransaction.CommitAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        databaseTransaction.DisposeAsync().Returns(ValueTask.CompletedTask);

        await using (var outer = new NhDbTransactionScope(databaseTransaction, isMyTransaction: true))
        {
            using (var nested = new NhDbTransactionScope(databaseTransaction, isMyTransaction: false))
            {
                await nested.CommitAsync();
                await nested.RollbackAsync();
            }

            await databaseTransaction.DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
            await databaseTransaction.DidNotReceive().RollbackAsync(Arg.Any<CancellationToken>());
            await outer.CommitAsync();
        }

        await databaseTransaction.Received(1).CommitAsync(Arg.Any<CancellationToken>());
        await databaseTransaction.DidNotReceive().RollbackAsync(Arg.Any<CancellationToken>());
        await databaseTransaction.Received(1).DisposeAsync();
    }

    [Fact]
    public void ConsumerIgnoresARedeliveredEventId()
    {
        var eventLog = new SampleEventLog();
        var message = new ProjectCreatedEvent
        {
            ProjectId = Guid.NewGuid(),
            ProjectKey = "RETRY-SAMPLE"
        };

        eventLog.Add(message);
        eventLog.Add(message);

        var consumed = Assert.Single(eventLog.Events);
        Assert.Equal(message.EventId, consumed.EventId);
    }

    [Fact]
    public async Task BulkContinueOnErrorCommitsSuccessfulItemsAndPublishesTheirCounts()
    {
        await using var dbContext = CreateDbContext();
        using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var existing = new Project
        {
            Id = Guid.NewGuid(),
            DivisionId = Guid.NewGuid(),
            Key = "BULK-1",
            Name = "Bulk sample",
            Status = ProjectStatus.Draft
        };
        dbContext.Projects.Add(existing);
        await dbContext.SaveChangesAsync();

        var databaseTransaction = Substitute.For<ITransaction>();
        databaseTransaction.CommitAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        databaseTransaction.DisposeAsync().Returns(ValueTask.CompletedTask);
        var outer = new NhDbTransactionScope(databaseTransaction, isMyTransaction: true);
        var nested = new NhDbTransactionScope(databaseTransaction, isMyTransaction: false);

        var repository = Substitute.For<IRepository<Project>>();
        repository.Context.Returns(dbContext);
        repository.GetAll().Returns(dbContext.Projects);
        repository.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(call => dbContext.SaveChangesAsync(call.Arg<CancellationToken>()));
        repository.StartOrGetTransactionScopeAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<INhDbTransactionScope>(outer), Task.FromResult<INhDbTransactionScope>(nested));

        var mapper = Substitute.For<IMapper>();
        mapper.Map<ProjectMutateModel>(Arg.Any<object>())
            .Returns(call => ToMutateModel((Project)call.Arg<object>()));
        mapper.Map(Arg.Any<ProjectMutateModel>(), Arg.Any<Project>())
            .Returns(call =>
            {
                var model = call.Arg<ProjectMutateModel>();
                var target = call.Arg<Project>();
                target.DivisionId = model.DivisionId;
                target.OwnerUserId = model.OwnerUserId;
                target.Key = model.Key;
                target.Name = model.Name;
                target.Description = model.Description;
                target.Status = model.Status;
                target.Deadline = model.Deadline;
                return target;
            });

        var localizer = Substitute.For<IStringLocalizer<ProjectService>>();
        localizer[Arg.Any<string>()]
            .Returns(call => new LocalizedString(call.Arg<string>(), call.Arg<string>()));
        localizer[Arg.Any<string>(), Arg.Any<object[]>()]
            .Returns(call => new LocalizedString(call.ArgAt<string>(0), call.ArgAt<string>(0)));
        var publisher = Substitute.For<INhEventPublisher>();
        publisher.PublishAsync(Arg.Any<ProjectBulkChangedEvent>()).Returns(Task.CompletedTask);
        var service = new ProjectService(
            repository,
            new NhDbLogService(
                Options.Create(new DbLogServiceSettings()),
                Substitute.For<IRepository<NhLog>>(),
                Substitute.For<IHttpContextAccessor>(),
                Substitute.For<IStringLocalizer<NhDbLogService>>(),
                Options.Create(new NewHeapAspNetCommonSettings())),
            new LogHelperService(Substitute.For<IStringLocalizer<SharedDataAnnotationRecources>>()),
            mapper,
            localizer,
            new ValidationService(serviceProvider),
            publisher);
        var missingId = Guid.NewGuid();

        var result = await service.BulkUpdateStatusAsync(new ProjectBulkStatusMutateModel
        {
            Ids = [existing.Id, missingId],
            Status = ProjectStatus.Completed,
            ContinueOnError = true
        });

        Assert.False(result.Success);
        Assert.Equal(2, result.Data?.RequestedCount);
        Assert.Equal(1, result.Data?.SucceededCount);
        Assert.Equal(1, result.Data?.FailedCount);
        Assert.Equal([missingId], result.Data?.FailedIds);
        Assert.Equal(2, result.Data?.Results.Count);
        Assert.True(result.Data?.Results.Single(item => item.Id == existing.Id).Success);
        Assert.NotEmpty(result.Data?.Results.Single(item => item.Id == missingId).ErrorMessages ?? []);
        Assert.Equal(ProjectStatus.Completed, existing.Status);
        await publisher.Received(1).PublishAsync(Arg.Is<ProjectBulkChangedEvent>(message =>
            message.Updated == 1 && message.Failed == 1));
        await databaseTransaction.Received(1).CommitAsync(Arg.Any<CancellationToken>());
        await databaseTransaction.DidNotReceive().RollbackAsync(Arg.Any<CancellationToken>());
        await repository.Received(2).StartOrGetTransactionScopeAsync(Arg.Any<CancellationToken>());
    }

    private static ProjectMutateModel ToMutateModel(Project project)
    {
        return new ProjectMutateModel
        {
            DivisionId = project.DivisionId,
            OwnerUserId = project.OwnerUserId,
            Key = project.Key,
            Name = project.Name,
            Description = project.Description,
            Status = project.Status,
            Deadline = project.Deadline
        };
    }

    private static SampleProjectManagementDbContext CreateDbContext()
    {
        return new SampleProjectManagementDbContext(
            new DbContextOptionsBuilder<SampleProjectManagementDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);
    }

    private static INhDbTransactionScope CreateTransaction(List<string> steps)
    {
        var transaction = Substitute.For<INhDbTransactionScope>();
        transaction.RollbackAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                steps.Add("rollback");
                return Task.CompletedTask;
            });
        transaction.CommitAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        transaction.DisposeAsync().Returns(ValueTask.CompletedTask);
        return transaction;
    }

    private static ProjectService CreateService(
        SampleProjectManagementDbContext dbContext,
        IServiceProvider serviceProvider,
        INhDbTransactionScope transaction,
        INhEventPublisher publisher,
        List<string> steps)
    {
        var repository = Substitute.For<IRepository<Project>>();
        repository.Context.Returns(dbContext);
        repository.GetAll().Returns(dbContext.Projects);
        repository.AddAsync(Arg.Any<Project>(), Arg.Any<CancellationToken>())
            .Returns(call => dbContext.Projects.AddAsync(
                call.Arg<Project>(),
                call.Arg<CancellationToken>()));
        repository.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                steps.Add("save");
                return await dbContext.SaveChangesAsync(call.Arg<CancellationToken>());
            });
        repository.StartOrGetTransactionScopeAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(transaction));

        var mapper = Substitute.For<IMapper>();
        mapper.Map<Project>(Arg.Any<object>())
            .Returns(call =>
            {
                var model = (ProjectMutateModel)call.Arg<object>();
                return new Project
                {
                    Id = Guid.NewGuid(),
                    DivisionId = model.DivisionId,
                    OwnerUserId = model.OwnerUserId,
                    Key = model.Key,
                    Name = model.Name,
                    Description = model.Description,
                    Status = model.Status,
                    Deadline = model.Deadline
                };
            });

        return new ProjectService(
            repository,
            new NhDbLogService(
                Options.Create(new DbLogServiceSettings()),
                Substitute.For<IRepository<NhLog>>(),
                Substitute.For<IHttpContextAccessor>(),
                Substitute.For<IStringLocalizer<NhDbLogService>>(),
                Options.Create(new NewHeapAspNetCommonSettings())),
            new LogHelperService(Substitute.For<IStringLocalizer<SharedDataAnnotationRecources>>()),
            mapper,
            Substitute.For<IStringLocalizer<ProjectService>>(),
            new ValidationService(serviceProvider),
            publisher);
    }

    private static ProjectMutateModel NewProject(string key)
    {
        return new ProjectMutateModel
        {
            DivisionId = Guid.NewGuid(),
            Key = key,
            Name = $"{key} project",
            Status = ProjectStatus.Active
        };
    }
}
