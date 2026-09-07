extern alias AutoMapper14;

using NewHeap.Platform.Mapping;
using Newtonsoft.Json;
using Xunit;

namespace NewHeap.Platform.Mapping.Tests;

public sealed class CircularReferenceParityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CollectionBackReferencesReuseTheRootLikeAutoMapper14(bool mapIntoExisting)
    {
        var mapper = CreateMapper();
        var referenceMapper = new AutoMapper14::AutoMapper.MapperConfiguration(configuration =>
        {
            configuration.CreateMap<Project, ProjectView>();
            configuration.CreateMap<Assignment, AssignmentView>();
        }).CreateMapper();
        var source = CreateProject();
        var existing = new ProjectView();

        var actual = mapIntoExisting
            ? mapper.Map(source, existing)
            : mapper.Map<ProjectView>(source);
        var expected = mapIntoExisting
            ? referenceMapper.Map(source, new ProjectView())
            : referenceMapper.Map<ProjectView>(source);

        Assert.Same(expected, expected.Assignments[0].Project);
        Assert.Same(actual, actual.Assignments[0].Project);
        if (mapIntoExisting)
        {
            Assert.Same(existing, actual);
        }

        var settings = new JsonSerializerSettings { ReferenceLoopHandling = ReferenceLoopHandling.Ignore };
        var json = JsonConvert.SerializeObject(actual, settings);
        Assert.Equal(JsonConvert.SerializeObject(expected, settings), json);
        Assert.NotNull(JsonConvert.DeserializeObject<ProjectView>(json,
            new JsonSerializerSettings { MaxDepth = 32 }));
    }

    [Fact]
    public void SeparateAndConcurrentOperationsHaveIndependentReferences()
    {
        var mapper = CreateMapper();
        var source = CreateProject();
        var results = Enumerable.Range(0, 16)
            .AsParallel()
            .Select(_ => mapper.Map<ProjectView>(source))
            .ToArray();

        Assert.Equal(results.Length, results.Distinct(ReferenceEqualityComparer.Instance).Count());
        Assert.All(results, result => Assert.Same(result, result.Assignments[0].Project));
    }

    [Fact]
    public void EqualButDistinctSourcesAreNotMistakenForCycles()
    {
        var mapper = new MapperConfiguration(configuration =>
            configuration.CreateMap<EqualNode, NodeView>()).CreateMapper();
        var source = new EqualNode { Child = new EqualNode() };
        var result = mapper.Map<NodeView>(source);

        Assert.NotSame(result, result.Child);
        Assert.NotNull(result.Child);
        Assert.Null(result.Child.Child);
    }

    [Fact]
    public void SelfReferenceRunsMappingActionsOnce()
    {
        var calls = 0;
        var mapper = new MapperConfiguration(configuration =>
            configuration.CreateMap<EqualNode, NodeView>()
                .AfterMap((_, _) => calls++)).CreateMapper();
        var source = new EqualNode();
        source.Child = source;

        var result = mapper.Map<NodeView>(source);

        Assert.Same(result, result.Child);
        Assert.Equal(1, calls);
    }

    private static IMapper CreateMapper()
    {
        return new MapperConfiguration(configuration =>
        {
            configuration.CreateMap<Project, ProjectView>();
            configuration.CreateMap<Assignment, AssignmentView>();
        }).CreateMapper();
    }

    private static Project CreateProject()
    {
        var project = new Project();
        project.Assignments.Add(new Assignment { Project = project });
        return project;
    }

    public sealed class Project
    {
        public List<Assignment> Assignments { get; set; } = [];
    }

    public sealed class Assignment
    {
        public Project? Project { get; set; }
    }

    public sealed class ProjectView
    {
        public List<AssignmentView> Assignments { get; set; } = [];
    }

    public sealed class AssignmentView
    {
        public ProjectView? Project { get; set; }
    }

    public sealed class EqualNode
    {
        public EqualNode? Child { get; set; }

        public override bool Equals(object? obj) => obj is EqualNode;

        public override int GetHashCode() => 0;
    }

    public sealed class NodeView
    {
        public NodeView? Child { get; set; }
    }
}
