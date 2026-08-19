using NewHeap.Platform.AspNet.Common.Models;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NewHeap.Platform.AspNet.Common.DAL.Entities;

public partial class NhDivision : NhDivision<NhDivisionUser, NhDivisionUserRole, NhDivisionRole, NhDivisionRoleClaim, NhDivision, NhUser>
{

}

public partial class NhDivision<TDivisionUser, TDivisionUserRole, TDivisionRole, TDivisionRoleClaim, TDivision, TUser> : IdDbEntity
    where TDivisionUser : NhDivisionUser<TDivisionUserRole, TDivisionUser, TDivisionRole, TDivisionRoleClaim, TDivision, TUser>
    where TDivisionUserRole : NhDivisionUserRole<TDivisionUser, TDivisionRole, TDivisionRoleClaim, TDivisionUserRole, TDivision, TUser>
    where TDivisionRole : NhDivisionRole<TDivisionUserRole, TDivisionRoleClaim, TDivisionUser, TDivisionRole, TDivision, TUser>
    where TDivisionRoleClaim : NhDivisionRoleClaim
    where TDivision : NhDivision<TDivisionUser, TDivisionUserRole, TDivisionRole, TDivisionRoleClaim, TDivision, TUser>
    where TUser : NhUser<TDivision, TDivisionUser, TDivisionUserRole, TDivisionRole, TDivisionRoleClaim, TUser>
{
    public NhDivision()
    {
        CreationDateTime = DateTimeOffset.UtcNow;
        LastModifiedDateTime = DateTimeOffset.UtcNow;
    }

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    public DateTimeOffset CreationDateTime { get; set; }

    public DateTimeOffset LastModifiedDateTime { get; set; }

    [StringLength(100)]
    public string Name { get; set; } = "";

    [StringLength(255)]
    public string Description { get; set; } = "";

    /// <summary>
    ///     The TimeZone id returned by the TimeZoneInfo.Id property.
    /// </summary>
    [StringLength(50)]
    public string TimeZoneId { get; set; } = "";

    public bool UserSelectAllowed { get; set; }

    public ICollection<TDivisionUser> DivisionUsers { get; set; } = new List<TDivisionUser>();
}