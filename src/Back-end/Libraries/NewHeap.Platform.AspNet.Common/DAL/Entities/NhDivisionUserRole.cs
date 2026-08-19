namespace NewHeap.Platform.AspNet.Common.DAL.Entities;

public partial class NhDivisionUserRole : NhDivisionUserRole<NhDivisionUser, NhDivisionRole, NhDivisionRoleClaim, NhDivisionUserRole, NhDivision, NhUser>
{
}

public partial class NhDivisionUserRole<TDivisionUser, TDivisionRole, TDivisionRoleClaim, TDivisionUserRole, TDivision, TUser>
    where TDivisionUser : NhDivisionUser<TDivisionUserRole, TDivisionUser, TDivisionRole, TDivisionRoleClaim, TDivision, TUser>
    where TDivisionRole : NhDivisionRole<TDivisionUserRole, TDivisionRoleClaim, TDivisionUser, TDivisionRole, TDivision, TUser>
    where TDivisionRoleClaim : NhDivisionRoleClaim
    where TDivisionUserRole : NhDivisionUserRole<TDivisionUser, TDivisionRole, TDivisionRoleClaim, TDivisionUserRole, TDivision, TUser>
    where TDivision : NhDivision<TDivisionUser, TDivisionUserRole, TDivisionRole, TDivisionRoleClaim, TDivision, TUser>
    where TUser : NhUser<TDivision, TDivisionUser, TDivisionUserRole, TDivisionRole, TDivisionRoleClaim, TUser>
{
    public Guid DivisionUserId { get; set; }
    public TDivisionUser DivisionUser { get; set; } = null!;
    public Guid DivisionRoleId { get; set; }
    public TDivisionRole DivisionRole { get; set; } = null!;
}