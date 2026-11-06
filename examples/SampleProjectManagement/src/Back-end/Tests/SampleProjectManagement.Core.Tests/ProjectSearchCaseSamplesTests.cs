using System.ComponentModel;
using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.PostgreSql;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Services;
using NewHeap.Platform.Mapping;
using SampleProjectManagement.Core.Models.View;
using SampleProjectManagement.Core.Utilities;
using SampleProjectManagement.DAL;
using SampleProjectManagement.DAL.Entities;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// SPM-016 and SPM-019: the project collection's free-text search and its <c>LIKE</c> text operator
/// ignore case on PostgreSQL, as they do on SQL Server's default collation, because the sample
/// configures its context with <c>UseNewHeapPostgreSql</c>. The opt-out
/// <c>UseNewHeapCaseSensitiveCollectionTextMatch</c> restores PostgreSQL's case-sensitive <c>LIKE</c>.
/// <see cref="CollectionAndProjectionSamplesTests.SearchAndLikeFilterIgnoreCaseOnEachProvider"/>
/// shows the SQL each provider runs.
/// </summary>
public sealed class ProjectSearchCaseSamplesTests(SamplePostgreSqlServer postgreSql)
{
    [Fact]
    public async Task SearchAndLikeFilterIgnoreCaseOnPostgreSql()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgreSql.CreateMigratedDatabaseAsync("search_case");

        // The sample's composition: UseNewHeapPostgreSql, as Program.cs configures the context.
        await using var dbContext = new SampleProjectManagementDbContext(
            new DbContextOptionsBuilder<SampleProjectManagementDbContext>()
                .UseNewHeapPostgreSql(connectionString)
                .Options);

        var divisionId = Guid.NewGuid();
        dbContext.Set<NhDivision>().Add(new NhDivision { Id = divisionId, Name = "Search case" });
        dbContext.Projects.AddRange(
            new Project { Id = Guid.NewGuid(), DivisionId = divisionId, Key = "NHP", Name = "Platform samples" },
            new Project { Id = Guid.NewGuid(), DivisionId = divisionId, Key = "MIG", Name = "PLATFORM migration" },
            new Project { Id = Guid.NewGuid(), DivisionId = divisionId, Key = "WEB", Name = "Website", Description = "Marketing platform" },
            new Project { Id = Guid.NewGuid(), DivisionId = divisionId, Key = "OPS", Name = "Operations" });
        await dbContext.SaveChangesAsync(cancellationToken);

        var processor = CreateProcessor();

        var search = await GetProjectsAsync(processor, dbContext, Search("platform"), cancellationToken);
        Assert.Equal(3, search.TotalCount);
        Assert.Equal(["PLATFORM migration", "Platform samples", "Website"], search.Items.Select(project => project.Name));

        var like = await GetProjectsAsync(processor, dbContext, NameLike("%platform%"), cancellationToken);
        Assert.Equal(["PLATFORM migration", "Platform samples"], like.Items.Select(project => project.Name));

        // Alternative: the opt-out keeps PostgreSQL's case-sensitive LIKE, so only exact case matches.
        await using var caseSensitiveContext = new SampleProjectManagementDbContext(
            new DbContextOptionsBuilder<SampleProjectManagementDbContext>()
                .UseNewHeapPostgreSql(connectionString, postgres => postgres.UseNewHeapCaseSensitiveCollectionTextMatch())
                .Options);

        var caseSensitiveSearch = await GetProjectsAsync(processor, caseSensitiveContext, Search("platform"), cancellationToken);
        Assert.Equal(["Website"], caseSensitiveSearch.Items.Select(project => project.Name));

        var caseSensitiveLike = await GetProjectsAsync(processor, caseSensitiveContext, NameLike("%platform%"), cancellationToken);
        Assert.Empty(caseSensitiveLike.Items);
    }

    /// <summary>The processing <c>GET /project</c> runs: the playground's view model and default order.</summary>
    private static Task<CollectionResultModel<ProjectViewModel>> GetProjectsAsync(
        CollectionProcessingService processor,
        SampleProjectManagementDbContext dbContext,
        CollectionRequestModel request,
        CancellationToken cancellationToken)
    {
        return processor.GetCollectionResultModelAsync<Project, ProjectViewModel>(
            request,
            dbContext.Projects,
            resultQueryableFunc: null,
            cancellationToken: cancellationToken,
            (project => project.Name, ListSortDirection.Ascending));
    }

    private static CollectionProcessingService CreateProcessor()
    {
        var mapper = new Mapper(new MapperConfiguration(configuration =>
            configuration.AddProfile<AutomapperProfileConfiguration>()));
        return new CollectionProcessingService(mapper);
    }

    private static CollectionRequestModel Search(string text)
    {
        return new CollectionRequestModel { Page = 1, ItemsPerPage = 10, Search = text };
    }

    private static CollectionRequestModel NameLike(string pattern)
    {
        return new CollectionRequestModel
        {
            Page = 1,
            ItemsPerPage = 10,
            Filter = [new FilterCollectionRequestModel { Key = "name", Operator = "LIKE", Value = pattern }]
        };
    }
}
