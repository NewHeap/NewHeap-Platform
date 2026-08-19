using NewHeap.Platform.Common.Attributes;
using NewHeap.Platform.Common.Models;
using SampleProjectManagement.DAL.Entities;

namespace SampleProjectManagement.Core.Models.View;

public class ProjectCollectionRequestModel : CollectionRequestModel
{
    public Guid? DivisionId { get; set; }

    public List<ProjectStatus> Statuses { get; set; } = [];
}

public class ProjectViewModel
{
    [Filterable]
    public Guid Id { get; set; }

    [Filterable]
    public Guid DivisionId { get; set; }

    [Filterable]
    public Guid? OwnerUserId { get; set; }

    [Searchable, Orderable, Filterable]
    public string Key { get; set; } = "";

    [Searchable, Orderable, Filterable]
    public string Name { get; set; } = "";

    [Searchable]
    public string? Description { get; set; }

    [Orderable, Filterable]
    public ProjectStatus Status { get; set; }

    [Orderable, Filterable]
    public DateTimeOffset? Deadline { get; set; }

    [Orderable, Filterable]
    public DateTimeOffset CreationDateTime { get; set; }

    [Orderable, Filterable]
    public DateTimeOffset LastModifiedDateTime { get; set; }
}

public class ProjectBulkStatusResultViewModel
{
    public int RequestedCount { get; set; }

    public List<ProjectBulkStatusItemResultViewModel> Results { get; set; } = [];

    public int SucceededCount { get; set; }

    public int FailedCount { get; set; }

    public List<Guid> FailedIds { get; set; } = [];
}

public class ProjectBulkStatusItemResultViewModel
{
    public Guid Id { get; set; }

    public bool Success { get; set; }

    public List<string> ErrorMessages { get; set; } = [];
}

/// <summary>
/// Result of the deliberate rollback sample. Both identifiers may be used to
/// verify through the project and consumed-event endpoints that neither side
/// effect escaped the uncommitted transaction.
/// </summary>
public sealed class ProjectRollbackSampleViewModel
{
    public Guid ProjectId { get; set; }

    public Guid EventId { get; set; }

    public string Verification { get; set; } =
        "The project must return 404 and the event must not occur in the consumed-event log.";
}
