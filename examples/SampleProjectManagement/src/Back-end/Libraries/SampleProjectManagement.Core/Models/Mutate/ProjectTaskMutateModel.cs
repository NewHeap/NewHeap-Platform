using NewHeap.Platform.Common.Attributes;
using System.ComponentModel.DataAnnotations;

namespace SampleProjectManagement.Core.Models.Mutate;

public class ProjectTaskMutateModel
{
    [NhRequired]
    public Guid ProjectId { get; set; }

    [NhRequired]
    [MaxLength(180)]
    public string Title { get; set; } = "";

    public bool IsCompleted { get; set; }
}
