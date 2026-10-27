using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Services;
using NewHeap.Platform.Mapping;
using NSubstitute;
using SampleProjectManagement.Core.Services;
using SampleProjectManagement.Core.Utilities;
using SampleProjectManagement.DAL;
using SampleProjectManagement.DAL.Entities;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// SPM-034: a nested filter key runs only through members that the project's filter views mark
/// <c>[Filterable]</c>, segment by segment, and a nested order key only through <c>[Orderable]</c>
/// members. A filter key that names an entity member the views do not expose, such as the owner's
/// password hash, fails with a safe error instead of filtering on it; such an order key is ignored.
/// <see cref="CollectionAndProjectionSamplesTests.NestedFilterKeysTranslateOnEachProvider"/>
/// translates the accepted keys for SQL Server and PostgreSQL.
/// </summary>
public sealed class ProjectNestedFilterSamplesTests(SamplePostgreSqlServer postgreSql)
{
    [Fact]
    public async Task NestedFiltersReachOnlyFilterableViewMembers()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgreSql.CreateMigratedDatabaseAsync("nested_filters");
        await using var dbContext = new SampleProjectManagementDbContext(
            new DbContextOptionsBuilder<SampleProjectManagementDbContext>()
                .UseNpgsql(connectionString)
                .Options);

        var divisionId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var completedByUserId = Guid.NewGuid();
        dbContext.Set<NhDivision>().Add(new NhDivision { Id = divisionId, Name = "Nested filters" });
        dbContext.Users.Add(new NhUser
        {
            Id = ownerId,
            UserName = "owner@example.test",
            NormalizedUserName = "OWNER@EXAMPLE.TEST",
            Email = "owner@example.test",
            PasswordHash = "stored-password-hash"
        });
        dbContext.Projects.AddRange(
            new Project
            {
                Id = Guid.NewGuid(),
                DivisionId = divisionId,
                OwnerUserId = ownerId,
                Key = "OWNED",
                Name = "Owned project",
                Tasks =
                [
                    new ProjectTask { Id = Guid.NewGuid(), Title = "Write the release note", IsCompleted = true, CompletedByUserId = completedByUserId }
                ]
            },
            new Project
            {
                Id = Guid.NewGuid(),
                DivisionId = divisionId,
                Key = "OTHER",
                Name = "Other project",
                Tasks = [new ProjectTask { Id = Guid.NewGuid(), Title = "Plan the sprint" }]
            });
        await dbContext.SaveChangesAsync(cancellationToken);

        var service = CreateService(dbContext);

        var byOwner = await service.GetByNestedFiltersAsync(Request("ownerUser.email", "owner@example.test"), cancellationToken);
        Assert.True(byOwner.Success);
        var owned = Assert.Single(byOwner.Data!.Items);
        Assert.Equal("Owned project", owned.Name);
        Assert.Equal("owner@example.test", owned.OwnerUser!.Email);

        var byTask = await service.GetByNestedFiltersAsync(Request("tasks{any}.title", "Plan the sprint"), cancellationToken);
        Assert.True(byTask.Success);
        Assert.Equal("Other project", Assert.Single(byTask.Data!.Items).Name);

        // The entities have these members, but the filter views do not mark them filterable.
        foreach (var (key, value) in new (string, object)[]
                 {
                     ("ownerUser.passwordHash", "stored-password-hash"),
                     ("ownerUser.userName", "owner@example.test"),
                     ("tasks{any}.completedByUserId", completedByUserId),
                     ("tasks.project.key", "OWNED")
                 })
        {
            var rejected = await service.GetByNestedFiltersAsync(Request(key, value), cancellationToken);

            Assert.False(rejected.Success);
            Assert.Null(rejected.Data);
            Assert.Equal("filter", Assert.Single(rejected.GetResultItems()).Name);
        }
    }

    /// <summary>
    /// An order key runs only through members the nested view marks <c>[Orderable]</c>. A key that
    /// continues behind the orderable <c>creationDateTime</c> into a conditional over the owner's
    /// password hash would let row order reveal the hash; it is ignored like an unknown key, so the
    /// projects keep the default order by name and nothing of it reaches the database.
    /// </summary>
    [Fact]
    public async Task NestedOrderKeysReachOnlyOrderableViewMembers()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgreSql.CreateMigratedDatabaseAsync("nested_orders");
        await using var dbContext = new SampleProjectManagementDbContext(
            new DbContextOptionsBuilder<SampleProjectManagementDbContext>()
                .UseNpgsql(connectionString)
                .Options);

        var divisionId = Guid.NewGuid();
        dbContext.Set<NhDivision>().Add(new NhDivision { Id = divisionId, Name = "Nested orders" });
        var created = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        foreach (var (key, email, passwordHash) in new[]
                 {
                     ("ALPHA", "b-owner@example.test", "z-hash"),
                     ("BETA", "a-owner@example.test", "a-hash")
                 })
        {
            var ownerId = Guid.NewGuid();
            dbContext.Users.Add(new NhUser
            {
                Id = ownerId,
                UserName = email,
                NormalizedUserName = email.ToUpperInvariant(),
                Email = email,
                PasswordHash = passwordHash
            });
            dbContext.Projects.Add(new Project
            {
                Id = Guid.NewGuid(),
                DivisionId = divisionId,
                OwnerUserId = ownerId,
                Key = key,
                Name = $"{key} project",
                CreationDateTime = created
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        var service = CreateService(dbContext);

        var byOwner = await service.GetByNestedFiltersAsync(OrderRequest("ownerUser.email"), cancellationToken);
        Assert.True(byOwner.Success);
        Assert.Equal(["BETA", "ALPHA"], byOwner.Data!.Items.Select(project => project.Key));

        // Ordering by the password hash would put BETA first; the default order by name puts ALPHA first.
        foreach (var key in new[]
                 {
                     "creationDateTime.year == 2026 ? ownerUser.passwordHash : \"\"",
                     "ownerUser.passwordHash",
                     "description",
                     "name.length",
                     "tasks.{first:asc}title"
                 })
        {
            var ignored = await service.GetByNestedFiltersAsync(OrderRequest(key), cancellationToken);

            Assert.True(ignored.Success);
            Assert.Equal(["ALPHA", "BETA"], ignored.Data!.Items.Select(project => project.Key));
            Assert.DoesNotContain(ignored.Data.OrderBy, order => order.Key == key);
        }
    }

    private static ProjectCollectionSampleService CreateService(SampleProjectManagementDbContext dbContext)
    {
        var mapper = new Mapper(new MapperConfiguration(configuration =>
            configuration.AddProfile<AutomapperProfileConfiguration>()));
        var repository = Substitute.For<IRepository<Project>>();
        repository.GetAll().Returns(dbContext.Projects);

        return new ProjectCollectionSampleService(repository, new CollectionProcessingService(mapper));
    }

    private static CollectionRequestModel OrderRequest(string key)
    {
        return new CollectionRequestModel
        {
            Page = 1,
            ItemsPerPage = 10,
            OrderBy = [new OrderByCollectionRequestModel { Key = key, Direction = "ASC" }]
        };
    }

    private static CollectionRequestModel Request(string key, object value)
    {
        return new CollectionRequestModel
        {
            Page = 1,
            ItemsPerPage = 10,
            Filter = [new FilterCollectionRequestModel { Key = key, Operator = "==", Value = value }]
        };
    }
}
