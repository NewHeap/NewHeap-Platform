using System.Globalization;
using NewHeap.Platform.Mapping;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

public sealed class MappingCompatibilitySamplesTests
{
    [Fact]
    public void ImportValuesUseTheRequestCultureAndEnumNames()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("nl-NL");
            var configuration = new MapperConfiguration(c => c.CreateMap<ProjectImportRow, ProjectImportMutateModel>());
            configuration.AssertConfigurationIsValid();
            var model = configuration.CreateMapper().Map<ProjectImportMutateModel>(new ProjectImportRow
            {
                Reference = "00112233-4455-6677-8899-aabbccddeeff",
                Budget = "1,25",
                Duration = "01:02:03",
                State = ImportedState.Ready
            });

            Assert.Equal(1.25m, model.Budget);
            Assert.Equal(ProjectState.Ready, model.State);
            Assert.Equal(TimeSpan.FromSeconds(3723), model.Duration);
            Assert.NotEqual(Guid.Empty, model.Reference);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void DerivedImportConditionProtectsInheritedFields()
    {
        var mapper = new MapperConfiguration(c =>
        {
            c.CreateMap<ProjectImportName, ProjectName>();
            c.CreateMap<ProjectImportDetail, ProjectDetail>()
                .IncludeBase<ProjectImportName, ProjectName>()
                .ForAllMembers(o => o.Condition((_, _, _, _) => false));
        }).CreateMapper();
        var destination = new ProjectDetail { Name = "Keep name", Detail = "Keep detail" };

        mapper.Map(new ProjectImportDetail { Name = "Incoming name", Detail = "Incoming detail" }, destination);

        Assert.Equal("Keep name", destination.Name);
        Assert.Equal("Keep detail", destination.Detail);
    }

    [Fact]
    public void BaseReadRequestsRetainDerivedDetail()
    {
        var mapper = new MapperConfiguration(c =>
        {
            c.CreateMap<ProjectImportName, ProjectName>();
            c.CreateMap<ProjectImportDetail, ProjectDetail>().IncludeBase<ProjectImportName, ProjectName>();
        }).CreateMapper();

        var model = mapper.Map<ProjectName>(new ProjectImportDetail { Name = "Project", Detail = "Details" });

        Assert.Equal("Details", Assert.IsType<ProjectDetail>(model).Detail);
    }

    [Fact]
    public void NullConvertersSupplyDefaultsWithoutRunningMappingActions()
    {
        var actionCalls = 0;
        var mapper = new MapperConfiguration(c =>
        {
            c.CreateMap<ProjectImportName, ProjectName>();
            c.CreateMap<string, string>()
                .AfterMap((_, _) => actionCalls++)
                .ConvertUsing(value => value ?? "Untitled project");
        }).CreateMapper();

        var model = mapper.Map<ProjectName>(new ProjectImportName());

        Assert.Equal("Untitled project", model.Name);
        Assert.Equal(0, actionCalls);
    }

    [Fact]
    public void ConventionMappingFlattensReadDataIntoPrivateSetters()
    {
        var configuration = new MapperConfiguration(c => c.CreateMap<ProjectOverviewSource, ProjectOverview>());
        configuration.AssertConfigurationIsValid();
        var model = configuration.CreateMapper().Map<ProjectOverview>(new ProjectOverviewSource
        {
            Owner = new ProjectName { Name = "Sample owner" }
        });

        Assert.Equal("Sample owner", model.OwnerName);
        Assert.Equal("Project overview", model.Label);
    }

    [Fact]
    public void LimitedReadGraphsHaveEmptyChildCollectionsAtTheBoundary()
    {
        var mapper = new MapperConfiguration(c => c.CreateMap<ProjectNode, ProjectNodeView>().MaxDepth(1)).CreateMapper();
        var model = mapper.Map<ProjectNodeView>(new ProjectNode { Children = [new ProjectNode()] });

        Assert.Empty(model.Children);
    }

    [Fact]
    public void SharedReadGraphMapsEachSourceNodeOnce()
    {
        var actionCalls = 0;
        var mapper = new MapperConfiguration(c => c.CreateMap<ProjectNode, ProjectNodeView>()
            .AfterMap((_, _) => actionCalls++)).CreateMapper();
        var root = new ProjectNode();
        for (var index = 1; index < 12; index++)
        {
            root = new ProjectNode { Children = [root, root] };
        }

        var model = mapper.Map<ProjectNodeView>(root);

        Assert.Same(model.Children[0], model.Children[1]);
        Assert.Equal(12, actionCalls);
    }

    public sealed class ProjectImportRow
    {
        public string? Reference { get; set; }
        public string? Budget { get; set; }
        public string? Duration { get; set; }
        public ImportedState State { get; set; }
    }

    public sealed class ProjectImportMutateModel
    {
        public Guid Reference { get; set; }
        public decimal Budget { get; set; }
        public TimeSpan Duration { get; set; }
        public ProjectState State { get; set; }
    }

    public class ProjectImportName
    {
        public string? Name { get; set; }
    }

    public sealed class ProjectImportDetail : ProjectImportName
    {
        public string? Detail { get; set; }
    }

    public class ProjectName
    {
        public string? Name { get; set; }
    }

    public sealed class ProjectDetail : ProjectName
    {
        public string? Detail { get; set; }
    }

    public sealed class ProjectOverviewSource
    {
        public ProjectName? Owner { get; set; }
        public string GetLabel() => "Project overview";
    }

    public sealed class ProjectOverview
    {
        public string? OwnerName { get; private set; }
        public string? Label { get; private set; }
    }

    public sealed class ProjectNode
    {
        public List<ProjectNode> Children { get; set; } = [];
    }

    public sealed class ProjectNodeView
    {
        public List<ProjectNodeView> Children { get; set; } = [];
    }

    public enum ImportedState
    {
        Ready = 1
    }

    public enum ProjectState
    {
        Ready = 10
    }
}
