using NewHeap.Platform.Common.Attributes;

namespace SampleProjectManagement.Core.Models.View;

/// <summary>
/// SPM-034: the filter and order contract of the nested-filter sample. Collection processing checks
/// every segment of a filter key against these view-model types, so <c>ownerUser.email</c> and
/// <c>tasks{any}.title</c> are accepted. <c>ownerUser.passwordHash</c> and
/// <c>tasks{any}.completedByUserId</c> are rejected, although the entities have those members:
/// the owner view has no password hash and the task view does not mark the completing user
/// <see cref="FilterableAttribute"/>. Order keys are checked the same way against
/// <see cref="OrderableAttribute"/>: <c>ownerUser.email</c> orders, while a key that continues behind
/// <c>creationDateTime</c>, such as a conditional over the owner's password hash, is ignored.
/// </summary>
public sealed class ProjectNestedFilterViewModel
{
    [Filterable]
    public Guid Id { get; set; }

    /// <summary>An orderable scalar; an order key ends here and cannot continue to its members.</summary>
    [Orderable]
    public DateTimeOffset CreationDateTime { get; set; }

    [Orderable]
    public string Key { get; set; } = "";

    [Orderable, Filterable]
    public string Name { get; set; } = "";

    /// <summary>
    /// A filterable and orderable navigation; only its own filterable or orderable members can be
    /// filtered or ordered on.
    /// </summary>
    [Filterable, Orderable]
    public ProjectNestedFilterOwnerViewModel? OwnerUser { get; set; }

    /// <summary>A filterable collection; a key continues inside its items, as in <c>tasks{any}.title</c>.</summary>
    [Filterable]
    public List<ProjectNestedFilterTaskViewModel> Tasks { get; set; } = [];
}

public sealed class ProjectNestedFilterOwnerViewModel
{
    [Filterable]
    public Guid Id { get; set; }

    [Filterable, Orderable]
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
