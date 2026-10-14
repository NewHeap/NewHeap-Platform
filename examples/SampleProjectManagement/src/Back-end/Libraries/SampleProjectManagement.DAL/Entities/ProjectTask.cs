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

    /// <summary>
    /// When the task was completed; <see langword="null"/> while it is open.
    /// </summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>
    /// The person who completed the task; <see langword="null"/> while it is open and when an agent
    /// completed it through an approved proposal.
    /// </summary>
    public Guid? CompletedByUserId { get; set; }
}
