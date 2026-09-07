using NewHeap.Platform.Mapping;
using Newtonsoft.Json;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

public sealed class CircularMappingSamplesTests
{
    [Fact]
    public void ProjectAssignmentBackReferenceIsIgnoredDuringJsonSerialization()
    {
        var mapper = new MapperConfiguration(configuration =>
        {
            configuration.CreateMap<ProjectReadSource, ProjectReadModel>();
            configuration.CreateMap<AssignmentReadSource, AssignmentReadModel>();
        }).CreateMapper();
        var project = new ProjectReadSource();
        project.Assignments.Add(new AssignmentReadSource { Project = project });

        var model = mapper.Map<ProjectReadModel>(project);

        Assert.Same(model, model.Assignments[0].Project);
        var json = JsonConvert.SerializeObject(model, new JsonSerializerSettings
        {
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore
        });
        var roundTrip = JsonConvert.DeserializeObject<ProjectReadModel>(json,
            new JsonSerializerSettings { MaxDepth = 32 });

        Assert.NotNull(roundTrip);
        Assert.Null(Assert.Single(roundTrip.Assignments).Project);
    }

    public sealed class ProjectReadSource
    {
        public List<AssignmentReadSource> Assignments { get; set; } = [];
    }

    public sealed class AssignmentReadSource
    {
        public ProjectReadSource? Project { get; set; }
    }

    public sealed class ProjectReadModel
    {
        public List<AssignmentReadModel> Assignments { get; set; } = [];
    }

    public sealed class AssignmentReadModel
    {
        public ProjectReadModel? Project { get; set; }
    }
}
