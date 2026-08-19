using NewHeap.Platform.Common.Attributes;

namespace SampleProjectManagement.Core.Models.Mutate;

public sealed class ProjectPlanningMutateModel
{
    public DateTimeOffset? Deadline { get; set; }

    public string? Description { get; set; }
}

public sealed class ProjectBulkUpdateItemMutateModel
{
    [NhRequired]
    public Guid Id { get; set; }

    [NhRequired]
    public ProjectMutateModel Model { get; set; } = new();
}

public sealed class ProjectBulkMutationSampleModel
{
    public List<ProjectMutateModel> Creates { get; set; } = [];

    public List<ProjectBulkUpdateItemMutateModel> Updates { get; set; } = [];

    public List<Guid> Deletes { get; set; } = [];

    public bool ContinueOnError { get; set; }
}

public sealed class ProjectWithInitialTaskMutateModel
{
    [NhRequired]
    public ProjectMutateModel Project { get; set; } = new();

    [NhRequired]
    public string InitialTaskTitle { get; set; } = "";
}
