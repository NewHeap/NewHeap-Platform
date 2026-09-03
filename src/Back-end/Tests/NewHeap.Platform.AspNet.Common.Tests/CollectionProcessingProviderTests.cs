using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Services;
using NewHeap.Platform.Mapping;
using NSubstitute;
using System.ComponentModel;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

public sealed class CollectionProcessingProviderTests
{
    [Fact]
    public async Task CountAndMaterializationWorkOnBothRelationalProviders()
    {
        await using (var sqlServer = new MsSqlBuilder(
            "mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04").Build())
        {
            await sqlServer.StartAsync();
            await VerifyProviderAsync(options => options.UseSqlServer(sqlServer.GetConnectionString()));
        }

        await using (var postgreSql = new PostgreSqlBuilder("postgres:15.1").Build())
        {
            await postgreSql.StartAsync();
            await VerifyProviderAsync(options => options.UseNpgsql(postgreSql.GetConnectionString()));
        }
    }

    private static async Task VerifyProviderAsync(Action<DbContextOptionsBuilder> configureProvider)
    {
        var optionsBuilder = new DbContextOptionsBuilder<CollectionProcessingDbContext>();
        configureProvider(optionsBuilder);

        await using var context = new CollectionProcessingDbContext(optionsBuilder.Options);
        await context.Database.EnsureCreatedAsync();
        context.Items.AddRange(
            new CollectionItem { Name = "First" },
            new CollectionItem { Name = "Second" });
        await context.SaveChangesAsync();

        var service = new CollectionProcessingService(Substitute.For<IMapper>());
        var request = new CollectionRequestModel
        {
            Page = 1,
            ItemsPerPage = 20
        };

        var result = await service.GetCollectionResultModelAsync<CollectionItem, CollectionItem>(
            request,
            context.Items,
            resultQueryableFunc: null,
            asNoTracking: true,
            cancellationToken: default,
            (item => (object)item.Id, ListSortDirection.Ascending));

        result.TotalCount.Should().Be(2);
        result.ResultCount.Should().Be(2);
        result.Items.Select(item => item.Name).Should().Equal("First", "Second");
    }

    private sealed class CollectionProcessingDbContext(
        DbContextOptions<CollectionProcessingDbContext> options)
        : DbContext(options)
    {
        public DbSet<CollectionItem> Items => Set<CollectionItem>();
    }

    private sealed class CollectionItem
    {
        public int Id { get; set; }
        public required string Name { get; set; }
    }
}
