using NewHeap.Platform.Mapping;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.Models;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Services;
using NewHeap.Platform.Common.Translations;
using NSubstitute;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

public class BaseDbEntityServiceTest
{
    [Fact]
    public async Task UpdatePartialAsync_ReturnsUpdatedEntityInData()
    {
        await using var dbContext = new UpdatePartialDbContext(
            new DbContextOptionsBuilder<UpdatePartialDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options
        );
        using var serviceProvider = new ServiceCollection().BuildServiceProvider();

        var entity = new TestEntity
        {
            Id = Guid.NewGuid(),
            Name = "Before"
        };
        dbContext.Entities.Add(entity);
        await dbContext.SaveChangesAsync();

        var repository = Substitute.For<IRepository<TestEntity>>();
        repository.Context.Returns(dbContext);
        repository.GetAll().Returns(dbContext.Entities);
        repository.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(call => dbContext.SaveChangesAsync(call.Arg<CancellationToken>()));

        var transaction = Substitute.For<INhDbTransactionScope>();
        repository.StartOrGetTransactionScopeAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(transaction));

        var mapper = Substitute.For<IMapper>();
        mapper.Map<TestMutateModel>(Arg.Any<object>())
            .Returns(call =>
            {
                var source = (TestEntity)call.Arg<object>();
                return new TestMutateModel { Name = source.Name };
            });
        mapper.Map(Arg.Any<TestMutateModel>(), Arg.Any<TestEntity>())
            .Returns(call =>
            {
                var source = call.Arg<TestMutateModel>();
                var destination = call.Arg<TestEntity>();
                destination.Name = source.Name;
                return destination;
            });

        var service = new TestEntityService(
            repository,
            Substitute.For<INhDbLogService>(),
            new LogHelperService(
                Substitute.For<IStringLocalizer<SharedDataAnnotationRecources>>(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<LogHelperService>.Instance),
            mapper,
            Substitute.For<IStringLocalizer<TestEntityService>>(),
            new ValidationService(serviceProvider)
        );

        var result = await service.UpdatePartialAsync(
            entity.Id,
            calls => calls.SetProperty(model => model.Name, "  After  "),
            options: new BaseDbEntityServiceOperationOptions
            {
                DbLoggingDisabled = true
            }
        );

        result.Success.Should().BeTrue();
        result.Data.Should().NotBeNull();
        result.Data!.Name.Should().Be("After");
        result.Data.Should().BeSameAs(entity);
        service.PartialUpdateMutateModelPrepared.Should().BeTrue();
        await transaction.Received(1).CommitAsync(Arg.Any<CancellationToken>());
    }

    public sealed class TestEntityService : BaseDbEntityService<TestEntity, TestMutateModel, TestEntityService>
    {
        public bool PartialUpdateMutateModelPrepared { get; private set; }

        public TestEntityService(
            IRepository<TestEntity> repository,
            INhDbLogService dbLogService,
            LogHelperService logHelperService,
            IMapper mapper,
            IStringLocalizer<TestEntityService> localizer,
            ValidationService validationService
        ) : base(repository, dbLogService, logHelperService, mapper, localizer, validationService)
        {
        }

        protected override Task PreparePartialUpdateMutateModelAsync(
            TestMutateModel mutateModel,
            CancellationToken cancellationToken = default)
        {
            PartialUpdateMutateModelPrepared = true;
            mutateModel.Name = mutateModel.Name.Trim();
            return Task.CompletedTask;
        }
    }

    private sealed class UpdatePartialDbContext(DbContextOptions<UpdatePartialDbContext> options)
        : DbContext(options)
    {
        public DbSet<TestEntity> Entities => Set<TestEntity>();
    }

    public sealed class TestEntity : IdDbEntity
    {
        public Guid Id { get; set; }

        public DateTimeOffset CreationDateTime { get; set; }

        public DateTimeOffset LastModifiedDateTime { get; set; }

        public string Name { get; set; } = string.Empty;
    }

    public sealed class TestMutateModel
    {
        public string Name { get; set; } = string.Empty;
    }
}
