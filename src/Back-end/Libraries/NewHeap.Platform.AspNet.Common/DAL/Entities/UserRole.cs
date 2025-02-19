using Microsoft.AspNetCore.Identity;

namespace NewHeap.Platform.AspNet.Common.DAL.Entities;

public partial class UserRole : IdentityRole<Guid>
{
    public UserRole()
    {
    }

    public UserRole(string roleName) : base(roleName)
    {
    }
}