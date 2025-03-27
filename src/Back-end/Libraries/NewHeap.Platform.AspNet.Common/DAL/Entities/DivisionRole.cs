using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NewHeap.Platform.AspNet.Common.DAL.Entities;

public partial class DivisionRole : DivisionRole<DivisionUserRole, DivisionRoleClaim, DivisionUser, DivisionRole, Division, User>
{
}

public partial class DivisionRole<TDivisionUserRole, TDivisionRoleClaim, TDivisionUser, TDivisionRole, TDivision, TUser>
    where TDivisionUserRole : DivisionUserRole<TDivisionUser, TDivisionRole, TDivisionRoleClaim, TDivisionUserRole, TDivision, TUser>
    where TDivisionRoleClaim : DivisionRoleClaim
    where TDivisionUser : DivisionUser<TDivisionUserRole, TDivisionUser, TDivisionRole, TDivisionRoleClaim, TDivision, TUser>
    where TDivisionRole : DivisionRole<TDivisionUserRole, TDivisionRoleClaim, TDivisionUser, TDivisionRole, TDivision, TUser>
    where TDivision : Division<TDivisionUser, TDivisionUserRole, TDivisionRole, TDivisionRoleClaim, TDivision, TUser>
    where TUser : User<TDivision, TDivisionUser, TDivisionUserRole, TDivisionRole, TDivisionRoleClaim, TUser>
{
    public DivisionRole()
    {
    }

    public DivisionRole(string roleName)
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