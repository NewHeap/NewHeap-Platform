using NewHeap.Platform.Common.Attributes;
using SampleProjectManagement.DAL.Entities;

namespace SampleProjectManagement.Core.Models.Mutate;

public class ProjectMemberMutateModel
{
    [NhRequired]
    public Guid ProjectId { get; set; }

    [NhRequired]
    public Guid UserId { get; set; }

    [NhRequired]
    public ProjectMemberRole Role { get; set; } = ProjectMemberRole.Reader;
}
