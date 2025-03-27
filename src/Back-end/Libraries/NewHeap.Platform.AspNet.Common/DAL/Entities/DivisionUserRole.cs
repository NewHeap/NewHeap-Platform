namespace NewHeap.Platform.AspNet.Common.DAL.Entities;

public partial class DivisionUserRole : DivisionUserRole<DivisionUser, DivisionRole, DivisionRoleClaim, DivisionUserRole>
{
}

public partial class DivisionUserRole<TDivisionUser, TDivisionRole, TDivisionRoleClaim, TDivisionUserRole>
    where TDivisionUser : DivisionUser<TDivisionUserRole, TDivisionUser, TDivisionRole, TDivisionRoleClaim>
    where TDivisionRole : DivisionRole<TDivisionUserRole, TDivisionRoleClaim, TDivisionUser, TDivisionRole>
    where TDivisionRoleClaim : DivisionRoleClaim
    where TDivisionUserRole : DivisionUserRole<TDivisionUser, TDivisionRole, TDivisionRoleClaim, TDivisionUserRole>
{
    public Guid DivisionUserId { get; set; }
    public TDivisionUser DivisionUser { get; set; } = null!;
    public Guid DivisionRoleId { get; set; }
    public TDivisionRole DivisionRole { get; set; } = null!;
}