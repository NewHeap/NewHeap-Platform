using NewHeap.Platform.Common.Attributes;
using SampleProjectManagement.DAL.Entities;
using System.ComponentModel.DataAnnotations;

namespace SampleProjectManagement.Core.Models.Mutate;

public class ProjectMutateModel
{
    [NhRequired]
    public Guid DivisionId { get; set; }

    public Guid? OwnerUserId { get; set; }

    [NhRequired]
    [MaxLength(30)]
    public string Key { get; set; } = "";

    [NhRequired]
    [MaxLength(150)]
    public string Name { get; set; } = "";

    [MaxLength(2000)]
    public string? Description { get; set; }

    public ProjectStatus Status { get; set; } = ProjectStatus.Draft;

    public DateTimeOffset? Deadline { get; set; }
}

public class ProjectStatusMutateModel
{
    public ProjectStatus Status { get; set; }
}

public class ProjectBulkStatusMutateModel
{
    [NhRequired]
    public List<Guid> Ids { get; set; } = [];

    public ProjectStatus Status { get; set; }

    public bool ContinueOnError { get; set; }
}
