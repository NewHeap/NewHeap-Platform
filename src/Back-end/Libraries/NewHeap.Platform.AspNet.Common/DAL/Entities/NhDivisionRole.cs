using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NewHeap.Platform.AspNet.Common.DAL.Entities;

public partial class NhDivisionRole : NhDivisionRole<NhDivisionUserRole, NhDivisionRoleClaim, NhDivisionUser, NhDivisionRole, NhDivision, NhUser>
{
}

public partial class NhDivisionRole<TDivisionUserRole, TDivisionRoleClaim, TDivisionUser, TDivisionRole, TDivision, TUser>
    where TDivisionUserRole : NhDivisionUserRole<TDivisionUser, TDivisionRole, TDivisionRoleClaim, TDivisionUserRole, TDivision, TUser>
    where TDivisionRoleClaim : NhDivisionRoleClaim
    where TDivisionUser : NhDivisionUser<TDivisionUserRole, TDivisionUser, TDivisionRole, TDivisionRoleClaim, TDivision, TUser>
    where TDivisionRole : NhDivisionRole<TDivisionUserRole, TDivisionRoleClaim, TDivisionUser, TDivisionRole, TDivision, TUser>
    where TDivision : NhDivision<TDivisionUser, TDivisionUserRole, TDivisionRole, TDivisionRoleClaim, TDivision, TUser>
    where TUser : NhUser<TDivision, TDivisionUser, TDivisionUserRole, TDivisionRole, TDivisionRoleClaim, TUser>
{
    public NhDivisionRole()
    {
    }

    public NhDivisionRole(string roleName)
    {
        Name = roleName;
    }

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    [StringLength(150)]
    public string Name { get; set; } = "";

    public ICollection<TDivisionUserRole> DivisionUserRoles { get; set; } = new List<TDivisionUserRole>();
    public ICollection<TDivisionRoleClaim> DivisionRoleClaims { get; set; } = new List<TDivisionRoleClaim>();
}