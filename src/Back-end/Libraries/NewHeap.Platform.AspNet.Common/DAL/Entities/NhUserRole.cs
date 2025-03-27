using Microsoft.AspNetCore.Identity;

namespace NewHeap.Platform.AspNet.Common.DAL.Entities;

public partial class NhUserRole : IdentityRole<Guid>
{
    public NhUserRole()
    {
    }

    public NhUserRole(string roleName) : base(roleName)
    {
    }
}