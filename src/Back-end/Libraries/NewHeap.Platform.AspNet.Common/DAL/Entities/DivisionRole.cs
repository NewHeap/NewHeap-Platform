using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NewHeap.Platform.AspNet.Common.DAL.Entities;

public partial class DivisionRole : DivisionRole<DivisionUserRole, DivisionRoleClaim, DivisionUser, DivisionRole>
{
}

public partial class DivisionRole<TDivisionUserRole, TDivisionRoleClaim, TDivisionUser, TDivisionRole>
    where TDivisionUserRole : DivisionUserRole<TDivisionUser, TDivisionRole, TDivisionRoleClaim>
    where TDivisionRoleClaim : DivisionRoleClaim
    where TDivisionUser : DivisionUser<DivisionUserRole<TDivisionUser, TDivisionRole, TDivisionRoleClaim>, TDivisionUser, TDivisionRole, TDivisionRoleClaim>
    where TDivisionRole : DivisionRole<DivisionUserRole<TDivisionUser, TDivisionRole, TDivisionRoleClaim>, TDivisionRoleClaim, TDivisionUser, TDivisionRole>
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