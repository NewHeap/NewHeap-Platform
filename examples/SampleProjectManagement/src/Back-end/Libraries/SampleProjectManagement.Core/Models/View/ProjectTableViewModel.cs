using System.Linq.Expressions;
using NewHeap.Platform.Common.Attributes;
using SampleProjectManagement.DAL.Entities;

namespace SampleProjectManagement.Core.Models.View;

public sealed class ProjectTableViewModel
{
    [Selectable, Filterable, Orderable]
    public Guid Id { get; set; }
    [Selectable, Searchable, Filterable, Orderable]
    public string Name { get; set; } = "";
    [Selectable, Searchable, Filterable, Orderable]
    public string Key { get; set; } = "";
    [Selectable, Filterable, Orderable]
    public ProjectStatus Status { get; set; }
    [Selectable, FieldAccess(Roles = "sample-project-manager,sample-security-officer")]
    public Guid? OwnerUserId { get; set; }
    [Selectable, Searchable, FieldAccess(Policy = "app.active-division.project.manage")]
    public string? Description { get; set; }
    [Selectable, Filterable, Orderable]
    public DateTimeOffset? Deadline { get; set; }

    public static readonly Expression<Func<Project, ProjectTableViewModel>> Projection = project => new()
    {
        Id = project.Id,
        Name = project.Name,
        Key = project.Key,
        Status = project.Status,
        OwnerUserId = project.OwnerUserId,
        Description = project.Description,
        Deadline = project.Deadline
    };
}
