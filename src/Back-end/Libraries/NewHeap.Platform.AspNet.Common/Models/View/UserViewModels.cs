using NewHeap.Platform.Common.Attributes;

namespace NewHeap.Platform.AspNet.Common.Models.View;

public partial class UserCollectionRequestModel
{
    public UserCollectionRequestModel()
    {
        Roles = new List<string>();
        DivisionIds = new List<Guid>();
    }

    public List<string> Roles { get; set; }
    public List<Guid> DivisionIds { get; set; }
    public bool ExcludeNonDivisionAccess { get; set; }
}

public class UserViewModel
{
    [Searchable]
    [Orderable]
    [Filterable]
    public Guid Id { get; set; }

    [Searchable]
    [Orderable]
    [Filterable]
    public string Email { get; set; }

    [Orderable]
    [Filterable]
    public bool EmailConfirmed { get; set; }

    [Orderable]
    [Filterable]
    public DateTimeOffset? LockoutEnd { get; set; }

    [Orderable]
    [Filterable]
    public DateTimeOffset? LockoutStart { get; set; }

    [Searchable]
    [Orderable]
    [Filterable]
    public string PhoneNumber { get; set; }

    [Orderable]
    [Filterable]
    public bool PhoneNumberConfirmed { get; set; }

    [Orderable]
    [Filterable]
    public DateTimeOffset CreationDateTime { get; set; }

    [Orderable]
    public Guid? ActiveDivisionId { get; set; }

    [Searchable]
    [Orderable]
    [Filterable]
    public DivisionViewModel ActiveDivision { get; set; }

    public ICollection<string> Roles { get; set; }
}