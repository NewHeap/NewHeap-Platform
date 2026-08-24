using NewHeap.Platform.Mapping;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models;
using NewHeap.Platform.AspNet.Common.Models.Options;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Events;
using NewHeap.Platform.Common.Services;
using NewHeap.Platform.Common.Translations;
using NSubstitute;
using SampleProjectManagement.Core.Events;
using SampleProjectManagement.Core.Models.Mutate;
using SampleProjectManagement.Core.Services;
using SampleProjectManagement.DAL;
using SampleProjectManagement.DAL.Entities;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// SPM-189–194: the application service owns the unit of work and
/// publishes its outbox event before the transaction is committed.
/// </summary>
public class ProjectUnitOfWorkSamplesTests
{
    [Fact]
    public async Task CreateSavesThenPublishesThenCommits()
    {
        await using var dbContext = new SampleProjectManagementDbContext(
            new DbContextOptionsBuilder<SampleProjectManagementDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);
        using var serviceProvider = new ServiceCollection().BuildServiceProvider();

        var steps = new List<string>();
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

        var transaction = Substitute.For<INhDbTransactionScope>();
        transaction.CommitAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                steps.Add("commit");
                return Task.CompletedTask;
            });
        transaction.DisposeAsync().Returns(ValueTask.CompletedTask);
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

        var eventPublisher = Substitute.For<INhEventPublisher>();
        eventPublisher.PublishAsync(Arg.Any<ProjectCreatedEvent>())
            .Returns(_ =>
            {
                steps.Add("publish");
                return Task.CompletedTask;
            });

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
            Substitute.For<IStringLocalizer<ProjectService>>(),
            new ValidationService(serviceProvider),
            eventPublisher);

        var divisionId = Guid.NewGuid();
        var result = await service.CreateAsync(
            new ProjectMutateModel
            {
                DivisionId = divisionId,
                Key = "  pm-42 ",
                Name = "  Transaction sample ",
                Status = ProjectStatus.Active
            },
            options: new BaseDbEntityServiceOperationOptions
            {
                DbLoggingDisabled = true
            });

        Assert.True(result.Success);
        Assert.Equal("PM-42", result.Data?.Key);
        Assert.Equal("Transaction sample", result.Data?.Name);
        Assert.Equal(["save", "publish", "commit"], steps);
        await eventPublisher.Received(1).PublishAsync(Arg.Is<ProjectCreatedEvent>(@event =>
            @event.ProjectId == result.Data!.Id && @event.ProjectKey == "PM-42"));
        await transaction.Received(1).CommitAsync(Arg.Any<CancellationToken>());

        var rejected = await service.CreateAsync(
            new ProjectMutateModel
            {
                DivisionId = divisionId,
                Key = "pm-42",
                Name = "Duplicate key",
                Status = ProjectStatus.Active
            },
            options: new BaseDbEntityServiceOperationOptions
            {
                DbLoggingDisabled = true
            });

        Assert.False(rejected.Success);
        Assert.Equal(["save", "publish", "commit"], steps);
    }
}
