namespace NewHeap.Platform.AspNet.Common.DAL.Entities;

public partial class DivisionUserRole : DivisionUserRole<DivisionUser, DivisionRole, DivisionRoleClaim>
{
}

public partial class DivisionUserRole<TDivisionUser, TDivisionRole, TDivisionRoleClaim>
    where TDivisionUser : DivisionUser<DivisionUserRole<TDivisionUser, TDivisionRole, TDivisionRoleClaim>, TDivisionUser, TDivisionRole, TDivisionRoleClaim>
    where TDivisionRole : DivisionRole<DivisionUserRole<TDivisionUser, TDivisionRole, TDivisionRoleClaim>, TDivisionRoleClaim, TDivisionUser, TDivisionRole>
    where TDivisionRoleClaim : DivisionRoleClaim
{
    public Guid DivisionUserId { get; set; }
    public Guid DivisionRoleId { get; set; }
}