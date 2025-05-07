using NewHeap.Platform.Common.Attributes;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AspNet.Common.Models.View;

public partial class DivisionCollectionRequestModel : CollectionRequestModel
{ 
    
}

public partial class NhDivisionViewModel
{
    [Searchable]
    [Filterable]
    public Guid Id { get; set; }

    [Searchable]
    [Orderable]
    [Filterable]
    public DateTimeOffset CreationDateTime { get; set; }

    [Searchable]
    [Orderable]
    [Filterable]
    public DateTimeOffset LastModifiedDateTime { get; set; }

    [Searchable]
    [Orderable]
    [Filterable]
    public string? Name { get; set; }

    public string? Description { get; set; }

    public string? TimeZoneId { get; set; }

    [Orderable]
    [Filterable]
    public bool UserSelectAllowed { get; set; }
}

public class DivisionUserCollectionRequestModel : CollectionRequestModel
{ 
    
}

public class DivisionUserViewModel<TUserViewModel, TDivisionViewModel, TDivisionRoleViewModel>
    where TUserViewModel : NhUserViewModel<TDivisionViewModel>
    where TDivisionViewModel : NhDivisionViewModel
    where TDivisionRoleViewModel : NhDivisionRoleViewModel
{
    [Searchable]
    [Filterable]
    public Guid Id { get; set; }

    [Searchable]
    [Filterable]
    public Guid UserId { get; set; }

    [Searchable]
    [Filterable]
    [Orderable]
    public TUserViewModel User { get; set; } = null!;

    [Searchable]
    [Filterable]
    public Guid DivisionId { get; set; }

    [Searchable]
    [Filterable]
    [Orderable]
    public TDivisionViewModel Division { get; set; } = null!;

    [Filterable]
    [Orderable]
    public DateTimeOffset? LockOutStartDateTime { get; set; }

    [Filterable]
    [Orderable]
    public DateTimeOffset? LockOutEndDateTime { get; set; }

    public ICollection<TDivisionRoleViewModel> Roles { get; set; } = new List<TDivisionRoleViewModel>();
}

public class NhDivisionRoleViewModel
{
    [Searchable]
    [Filterable]
    public Guid Id { get; set; }

    [Searchable]
    [Filterable]
    [Orderable]
    public string Name { get; set; } = null!;
}