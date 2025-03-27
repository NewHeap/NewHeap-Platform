using Microsoft.AspNetCore.Identity;
using System.ComponentModel.DataAnnotations;

namespace NewHeap.Platform.AspNet.Common.DAL.Entities;

public class User : User<
    Division, 
    DivisionUser, 
    DivisionUserRole,
    DivisionRole,
    DivisionRoleClaim,
    User
    >
{
}

public partial class User<TDivision, TDivisionUser, TDivisionUserRole, TDivisionRole, TDivisionRoleClaim, TUser> : IdentityUser<Guid>
    where TDivision : Division<TDivisionUser, TDivisionUserRole, TDivisionRole, TDivisionRoleClaim, TDivision, TUser>
    where TDivisionUser : DivisionUser<TDivisionUserRole, TDivisionUser, TDivisionRole, TDivisionRoleClaim, TDivision, TUser>
    where TDivisionUserRole : DivisionUserRole<TDivisionUser, TDivisionRole, TDivisionRoleClaim, TDivisionUserRole, TDivision, TUser>
    where TDivisionRole : DivisionRole<TDivisionUserRole, TDivisionRoleClaim, TDivisionUser, TDivisionRole, TDivision, TUser>
    where TDivisionRoleClaim : DivisionRoleClaim
    where TUser : User<TDivision, TDivisionUser, TDivisionUserRole, TDivisionRole, TDivisionRoleClaim, TUser>
{
    public User()
    {
        CreationDateTime = DateTimeOffset.UtcNow;
    }

    public DateTimeOffset CreationDateTime { get; set; }

    [Display(Name = "ActiveDivision")]
    public Guid? ActiveDivisionId { get; set; }

    public TDivision? ActiveDivision { get; set; }

    /// <summary>
    ///     Unlike <see cref="IdentityUser.LockoutEnd" /> this property not part of the identity framework. Therefore this
    ///     value will not influence <see cref="IdentityUser.LockoutEnabled" />.
    ///     Therefore when evaluating if a user is locked out you should always separately evaluate this field.
    /// </summary>
    public DateTimeOffset? LockoutStart { get; set; }

    public ICollection<TDivisionUser> DivisionUsers { get; set; } = new List<TDivisionUser>();
    
    [StringLength(100)]
    public string RefreshToken { get; set; }
}