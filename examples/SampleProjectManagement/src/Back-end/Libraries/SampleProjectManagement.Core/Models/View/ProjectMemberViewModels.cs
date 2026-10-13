using NewHeap.Platform.Common.Attributes;
using NewHeap.Platform.Common.Models;
using SampleProjectManagement.DAL.Entities;

namespace SampleProjectManagement.Core.Models.View;

public class ProjectMemberCollectionRequestModel : CollectionRequestModel
{
    public Guid? ProjectId { get; set; }
}

public class ProjectMemberViewModel
{
    [Filterable]
    public Guid Id { get; set; }

    [Filterable]
    public Guid ProjectId { get; set; }

    [Filterable]
    public Guid UserId { get; set; }

    [Searchable, Orderable, Filterable]
    public string UserName { get; set; } = "";

    [Orderable, Filterable]
    public ProjectMemberRole Role { get; set; }

    [Orderable, Filterable]
    public DateTimeOffset CreationDateTime { get; set; }

    [Orderable, Filterable]
    public DateTimeOffset LastModifiedDateTime { get; set; }
}
