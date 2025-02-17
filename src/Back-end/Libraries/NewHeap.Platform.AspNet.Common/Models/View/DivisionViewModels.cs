using NewHeap.Platform.Common.Attributes;
using System;
using System.Collections.Generic;

namespace NewHeap.Platform.AspNet.Common.Models.View;

public partial class DivisionViewModel
{
    [Searchable, Filterable]
    public Guid Id { get; set; }

    [Searchable, Orderable, Filterable]
    public DateTimeOffset CreationDateTime { get; set; }

    [Searchable, Orderable, Filterable]
    public DateTimeOffset LastModifiedDateTime { get; set; }

    [Searchable, Orderable, Filterable]
    public string Name { get; set; }

    public string Description { get; set; }

    public string TimeZoneId { get; set; }

    [Orderable, Filterable]
    public bool UserSelectAllowed { get; set; }
}

public partial class DivisionUserViewModel
{
    [Searchable, Filterable]
    public Guid Id { get; set; }

    [Searchable, Filterable]
    public Guid UserId { get; set; }

    [Searchable, Filterable, Orderable]
    public UserViewModel User { get; set; }

    [Searchable, Filterable]
    public Guid DivisionId { get; set; }

    [Searchable, Filterable, Orderable]
    public DivisionViewModel Division { get; set; }

    [Filterable, Orderable]
    public DateTimeOffset? LockOutStartDateTime { get; set; }

    [Filterable, Orderable]
    public DateTimeOffset? LockOutEndDateTime { get; set; }

    public ICollection<DivisionRoleViewModel> Roles { get; set; } = new List<DivisionRoleViewModel>();
}

public class DivisionRoleViewModel
{
    [Searchable, Filterable]
    public Guid Id { get; set; }

    [Searchable, Filterable, Orderable]
    public string Name { get; set; }
}
