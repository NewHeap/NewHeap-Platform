using Microsoft.AspNetCore.Identity;
using System.ComponentModel.DataAnnotations;

namespace NewHeap.Platform.AspNet.Common.DAL.Entities;

public partial class User : IdentityUser<Guid>
{
    public User()
    {
        CreationDateTime = DateTimeOffset.UtcNow;
    }

    public DateTimeOffset CreationDateTime { get; set; }

    [Display(Name = "ActiveDivision")]
    public Guid? ActiveDivisionId { get; set; }

    public Division? ActiveDivision { get; set; }

    /// <summary>
    ///     Unlike <see cref="IdentityUser.LockoutEnd" /> this property not part of the identity framework. Therefore this
    ///     value will not influence <see cref="IdentityUser.LockoutEnabled" />.
    ///     Therefore when evaluating if a user is locked out you should always separately evaluate this field.
    /// </summary>
    public DateTimeOffset? LockoutStart { get; set; }

    public ICollection<DivisionUser> DivisionUsers { get; set; } = new List<DivisionUser>();
}