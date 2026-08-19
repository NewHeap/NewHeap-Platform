using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SampleProjectManagement.DAL.Entities;

public class Project : IdDbEntity
{
    public Project()
    {
        CreationDateTime = DateTimeOffset.UtcNow;
        LastModifiedDateTime = DateTimeOffset.UtcNow;
    }

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    public DateTimeOffset CreationDateTime { get; set; }

    public DateTimeOffset LastModifiedDateTime { get; set; }

    public Guid DivisionId { get; set; }

    public NhDivision Division { get; set; } = null!;

    public Guid? OwnerUserId { get; set; }

    public NhUser? OwnerUser { get; set; }

    [StringLength(30)]
    public string Key { get; set; } = "";

    [StringLength(150)]
    public string Name { get; set; } = "";

    [StringLength(2000)]
    public string? Description { get; set; }

    public ProjectStatus Status { get; set; } = ProjectStatus.Draft;

    public DateTimeOffset? Deadline { get; set; }

    public ICollection<ProjectTask> Tasks { get; set; } = [];
}
