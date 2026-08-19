using NewHeap.Platform.Common.Attributes;
using NewHeap.Platform.Common.Models;

namespace SampleProjectManagement.Core.Models.View;

public class ProjectTaskCollectionRequestModel : CollectionRequestModel
{
    public Guid? ProjectId { get; set; }
}

public class ProjectTaskViewModel
{
    [Filterable]
    public Guid Id { get; set; }

    [Filterable]
    public Guid ProjectId { get; set; }

    [Searchable, Orderable, Filterable]
    public string Title { get; set; } = "";

    [Orderable, Filterable]
    public bool IsCompleted { get; set; }

    [Orderable, Filterable]
    public DateTimeOffset CreationDateTime { get; set; }

    [Orderable, Filterable]
    public DateTimeOffset LastModifiedDateTime { get; set; }
}
