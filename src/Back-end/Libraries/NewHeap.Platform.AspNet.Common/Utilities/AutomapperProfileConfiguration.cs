using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models.View;
using System.Security.Claims;

namespace NewHeap.Platform.AspNet.Common.Utilities;

public class AutomapperProfileConfiguration : AutoMapper.Profile
{
    public AutomapperProfileConfiguration()
        : this("NewHeapPlatformCommonProfile")
    {
    }

    protected AutomapperProfileConfiguration(string profileName)
        : base(profileName)
    {
        CreateMap<User, UserViewModel>();
        CreateMap<Division, DivisionViewModel>();
        CreateMap<Claim, ClaimViewModel>();
    }
}