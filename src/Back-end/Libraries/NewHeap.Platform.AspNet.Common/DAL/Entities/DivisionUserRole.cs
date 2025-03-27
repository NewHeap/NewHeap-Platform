namespace NewHeap.Platform.AspNet.Common.DAL.Entities;

public partial class DivisionUserRole : DivisionUserRole<DivisionUser, DivisionRole, DivisionRoleClaim, DivisionUserRole, NhDivision, User>
{
}

public partial class DivisionUserRole<TDivisionUser, TDivisionRole, TDivisionRoleClaim, TDivisionUserRole, TDivision, TUser>
    where TDivisionUser : DivisionUser<TDivisionUserRole, TDivisionUser, TDivisionRole, TDivisionRoleClaim, TDivision, TUser>
    where TDivisionRole : DivisionRole<TDivisionUserRole, TDivisionRoleClaim, TDivisionUser, TDivisionRole, TDivision, TUser>
    where TDivisionRoleClaim : DivisionRoleClaim
    where TDivisionUserRole : DivisionUserRole<TDivisionUser, TDivisionRole, TDivisionRoleClaim, TDivisionUserRole, TDivision, TUser>
    where TDivision : Division<TDivisionUser, TDivisionUserRole, TDivisionRole, TDivisionRoleClaim, TDivision, TUser>
    where TUser : User<TDivision, TDivisionUser, TDivisionUserRole, TDivisionRole, TDivisionRoleClaim, TUser>
{
    public Guid DivisionUserId { get; set; }
    public TDivisionUser DivisionUser { get; set; } = null!;
    public Guid DivisionRoleId { get; set; }
    public TDivisionRole DivisionRole { get; set; } = null!;
}