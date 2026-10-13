using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SampleProjectManagement.DAL.Entities;

/// <summary>
/// A person on a project. The role decides what the person may do with the project's agent
/// threads: read them, take part, or decide the agent's proposals.
/// </summary>
public class ProjectMember : IdDbEntity
{
    public ProjectMember()
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

    public Guid UserId { get; set; }

    public NhUser User { get; set; } = null!;

    public ProjectMemberRole Role { get; set; } = ProjectMemberRole.Reader;
}

/// <summary>
/// Project roles, ordered so that each role includes the ones below it.
/// </summary>
public enum ProjectMemberRole
{
    Reader = 10,
    Participant = 20,
    Approver = 30
}
