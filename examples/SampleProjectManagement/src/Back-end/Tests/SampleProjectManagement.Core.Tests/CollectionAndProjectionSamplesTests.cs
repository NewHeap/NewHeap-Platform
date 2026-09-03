using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NewHeap.Platform.Common;
using NewHeap.Platform.Mapping;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Models.Options;
using NewHeap.Platform.Common.Services;
using SampleProjectManagement.Core.Models.View;
using SampleProjectManagement.DAL.Entities;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

public class CollectionAndProjectionSamplesTests
{
    private readonly CollectionProcessingService _processor;

    public CollectionAndProjectionSamplesTests()
    {
        var mapper = new Mapper(new MapperConfiguration(configuration =>
            configuration.CreateMap<Project, ProjectViewModel>()));
        _processor = new CollectionProcessingService(mapper);
    }

    [Fact]
    public void InMemoryProcessingSupportsFilterAndOrder()
    {
        IQueryable<Project> projects = new[]
        {
            Project("OPS", "Operations", ProjectStatus.Active),
            Project("WEB", "Website", ProjectStatus.Completed),
            Project("APP", "Operations app", ProjectStatus.Active)
        }.AsQueryable();

        _processor.ProcessFilter<Project, ProjectViewModel>(ref projects,
        [
            new FilterCollectionRequestModel
            {
                Key = "status",
                Operator = "==",
                Value = ProjectStatus.Active
            }
        ]);
        _processor.ProcessOrderBy<Project, ProjectViewModel>(ref projects,
        [
            new OrderByCollectionRequestModel { Key = "name", Direction = "DESC" }
        ]);

        Assert.Equal(["Operations app", "Operations"], projects.Select(project => project.Name));
    }

    [Fact]
    public void ExpressionSelectorsRejectMethodCallsAndAcceptMemberPaths()
    {
        var builder = new CollectionProcessingOptionsBuilder<Project, ProjectProjectionViewModel>()
            .WithFilterable(project => project.Status)
            .WithOrderable(project => project.Name)
            .WithSearchable(project => project.Key, project => project.Name);

        Assert.Throws<ArgumentException>(() => builder.WithFilterable(project => project.Name.ToLowerInvariant()));
    }

    [Fact]
    public void CollectionProcessingSettingsBindFromPlatformCommonSettings()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NewHeap:PlatformCommon:Settings:CollectionProcessingDefaultItemsPerPage"] = "25",
                ["NewHeap:PlatformCommon:Settings:CollectionProcessingDefaultMaxItemsPerPage"] = "250",
                ["NewHeap:PlatformCommon:Settings:CollectionProcessingDeadlockMaxAttempts"] = "2"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddNewHeapPlatformCommon(NewHeapCommonOptions.Builder(configuration).Build());
        using var serviceProvider = services.BuildServiceProvider();

        var settings = serviceProvider
            .GetRequiredService<IOptions<NewHeapCommonSettings>>()
            .Value;

        Assert.Equal(25, settings.CollectionProcessingDefaultItemsPerPage);
        Assert.Equal(250, settings.CollectionProcessingDefaultMaxItemsPerPage);
        Assert.Equal(2, settings.CollectionProcessingDeadlockMaxAttempts);
    }

    [Fact]
    public void ProjectionBuilderCreatesCalculatedFieldsWithoutIncludeAll()
    {
        var projection = NhProjection
            .For<Project, ProjectProjectionViewModel>()
            .Map(view => view.Id, project => project.Id)
            .Map(view => view.DisplayName, project => project.Key + " · " + project.Name)
            .Map(view => view.OpenTaskCount, project => project.Tasks.Count(task => !task.IsCompleted))
            .Build();

        var mapped = projection.Compile()(Project("NHP", "Platform samples", ProjectStatus.Active));

        Assert.Equal("NHP · Platform samples", mapped.DisplayName);
        Assert.Equal(0, mapped.OpenTaskCount);
    }

    private static Project Project(string key, string name, ProjectStatus status) => new()
    {
        Id = Guid.NewGuid(),
        DivisionId = Guid.NewGuid(),
        Key = key,
        Name = name,
        Status = status
    };
}
