using NewHeap.Platform.AspNet.Common.Models;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NewHeap.Platform.AspNet.Common.DAL.Entities;

public partial class NhDivisionUser : NhDivisionUser<NhDivisionUserRole, NhDivisionUser, NhDivisionRole, NhDivisionRoleClaim, NhDivision, NhUser>
{
}

public partial class NhDivisionUser<TDivisionUserRole, TDivisionUser, TDivisionRole, TDivisionRoleClaim, TDivision, TUser> : IdDbEntity
    where TDivisionUserRole : NhDivisionUserRole<TDivisionUser, TDivisionRole, TDivisionRoleClaim, TDivisionUserRole, TDivision, TUser>
    where TDivisionUser : NhDivisionUser<TDivisionUserRole, TDivisionUser, TDivisionRole, TDivisionRoleClaim, TDivision, TUser>
    where TDivisionRole : NhDivisionRole<TDivisionUserRole, TDivisionRoleClaim, TDivisionUser, TDivisionRole, TDivision, TUser>
    where TDivisionRoleClaim : NhDivisionRoleClaim
    where TDivision : NhDivision<TDivisionUser, TDivisionUserRole, TDivisionRole, TDivisionRoleClaim, TDivision, TUser>
    where TUser : NhUser<TDivision, TDivisionUser, TDivisionUserRole, TDivisionRole, TDivisionRoleClaim, TUser>
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }
    public DateTimeOffset CreationDateTime { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastModifiedDateTime { get; set; } = DateTimeOffset.UtcNow;

    public Guid UserId { get; set; }

    public TUser User { get; set; } = null!;

    public Guid DivisionId { get; set; }

    public TDivision Division { get; set; } = null!;

    public DateTimeOffset? LockOutStartDateTime { get; set; }
    public DateTimeOffset? LockOutEndDateTime { get; set; }

    public ICollection<TDivisionUserRole> DivisionUserRoles { get; set; } = new List<TDivisionUserRole>();
}