using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NewHeap.Platform.AspNet.Common.DAL.Entities;

public partial class DivisionRole
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

    public ICollection<DivisionUserRole> DivisionUserRoles { get; set; } = new List<DivisionUserRole>();
    public ICollection<DivisionRoleClaim> DivisionRoleClaims { get; set; } = new List<DivisionRoleClaim>();
}