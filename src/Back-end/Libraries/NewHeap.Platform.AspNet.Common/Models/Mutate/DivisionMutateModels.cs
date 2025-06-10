using NewHeap.Platform.Common.Attributes;
using System.ComponentModel.DataAnnotations;

namespace NewHeap.Platform.AspNet.Common.Models.Mutate;

public partial class NhDivisionMutateModel
{
    [NhRequired]
    [StringLength(100)]
    public string? Name { get; set; }

    [StringLength(255)]
    public string Description { get; set; } = "";

    public bool UserSelectAllowed { get; set; }


    [StringLength(50, MinimumLength = 1)]
    public string? TimeZoneId { get; set; }
}

public class NhDivisionUserMutateModel
{
    [NhRequired]
    [Display(Name = "Division")]
    public Guid? DivisionId { get; set; }

    [NhRequired]
    [Display(Name = "User")]
    public Guid? UserId { get; set; }

    [Display(Name = "Roles")]
    public List<Guid> RoleIds { get; set; } = new();

    public DateTimeOffset? LockOutStartDateTime { get; set; }
    public DateTimeOffset? LockOutEndDateTime { get; set; }
}