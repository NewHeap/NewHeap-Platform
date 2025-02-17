using Microsoft.AspNetCore.Identity;
using System;

namespace NewHeap.Platform.AspNet.Common.DAL.Entities;

public partial class UserRole : IdentityRole<Guid>
{
    public UserRole() : base()
    {

    }

    public UserRole(string roleName) : base(roleName)
    { 
    
    }
}
