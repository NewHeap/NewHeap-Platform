using NewHeap.Platform.AspNet.Common.Models;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SampleProjectManagement.DAL.Entities;

public class ProjectTask : IdDbEntity
{
    public ProjectTask()
    {
        CreationDateTime = DateTimeOffset.UtcNow;
        LastModifiedDateTime = DateTimeOffset.UtcNow;
    }

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    public DateTimeOffset CreationDateTime { get; set; }

    public DateTimeOffset LastModifiedDateTime { get; set; }

    public Guid ProjectId { get; set; }

    public Project Project { get; set; } = null!;

    [StringLength(180)]
    public string Title { get; set; } = "";

    public bool IsCompleted { get; set; }
}
