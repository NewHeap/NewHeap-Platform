using NewHeap.Platform.AspNet.Common.Models;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NewHeap.Platform.AspNet.Common.DAL.Entities;

public partial class DivisionUser : DivisionUser<DivisionUserRole, DivisionUser, DivisionRole, DivisionRoleClaim>
{
}

public partial class DivisionUser<TDivisionUserRole, TDivisionUser, TDivisionRole, TDivisionRoleClaim> : IdDbEntity
    where TDivisionUserRole : DivisionUserRole<TDivisionUser, TDivisionRole, TDivisionRoleClaim, TDivisionUserRole>
    where TDivisionUser : DivisionUser<TDivisionUserRole, TDivisionUser, TDivisionRole, TDivisionRoleClaim>
    where TDivisionRole : DivisionRole<TDivisionUserRole, TDivisionRoleClaim, TDivisionUser, TDivisionRole>
    where TDivisionRoleClaim : DivisionRoleClaim
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }
    public DateTimeOffset CreationDateTime { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastModifiedDateTime { get; set; } = DateTimeOffset.UtcNow;

    public Guid UserId { get; set; }

    public Guid DivisionId { get; set; }

    public DateTimeOffset? LockOutStartDateTime { get; set; }
    public DateTimeOffset? LockOutEndDateTime { get; set; }

    public ICollection<TDivisionUserRole> DivisionUserRoles { get; set; } = new List<TDivisionUserRole>();
}