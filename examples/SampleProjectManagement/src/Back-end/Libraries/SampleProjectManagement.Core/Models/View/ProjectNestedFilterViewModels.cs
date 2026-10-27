using NewHeap.Platform.Common.Attributes;

namespace SampleProjectManagement.Core.Models.View;

/// <summary>
/// SPM-034: the filter contract of the nested-filter sample. Collection processing checks every
/// segment of a filter key against these view-model types, so <c>ownerUser.email</c> and
/// <c>tasks{any}.title</c> are accepted. <c>ownerUser.passwordHash</c> and
/// <c>tasks{any}.completedByUserId</c> are rejected, although the entities have those members:
/// the owner view has no password hash and the task view does not mark the completing user
/// <see cref="FilterableAttribute"/>.
/// </summary>
public sealed class ProjectNestedFilterViewModel
{
    [Filterable]
    public Guid Id { get; set; }

    [Orderable]
    public string Key { get; set; } = "";

    [Orderable, Filterable]
    public string Name { get; set; } = "";

    /// <summary>A filterable navigation; only its own filterable members can be filtered.</summary>
    [Filterable]
    public ProjectNestedFilterOwnerViewModel? OwnerUser { get; set; }

    /// <summary>A filterable collection; a key continues inside its items, as in <c>tasks{any}.title</c>.</summary>
    [Filterable]
    public List<ProjectNestedFilterTaskViewModel> Tasks { get; set; } = [];
}

public sealed class ProjectNestedFilterOwnerViewModel
{
    [Filterable]
    public Guid Id { get; set; }

    [Filterable]
    public string? Email { get; set; }
}

public sealed class ProjectNestedFilterTaskViewModel
{
    [Filterable]
    public Guid Id { get; set; }

    [Filterable]
    public string Title { get; set; } = "";

    [Filterable]
    public bool IsCompleted { get; set; }

    /// <summary>Returned, but not filterable: a filter key that ends here is rejected.</summary>
    public Guid? CompletedByUserId { get; set; }
}
