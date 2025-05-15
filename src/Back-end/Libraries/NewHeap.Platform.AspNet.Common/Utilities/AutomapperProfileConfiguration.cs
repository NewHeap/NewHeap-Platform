using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models.Mutate;
using NewHeap.Platform.AspNet.Common.Models.View;
using NewHeap.Platform.Common;
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
        CreateMap<Claim, NhClaimViewModel>();
        CreateMap<NhUser, NhUserViewModel<NhDivisionViewModel>>();
        CreateMap<NhDivision, NhDivisionViewModel>();
        CreateMap<NhDivisionUser, DivisionUserViewModel<NhUserViewModel<NhDivisionViewModel>, NhDivisionViewModel, NhDivisionRoleViewModel>>();
        CreateMap<NhDivisionRole, NhDivisionRoleViewModel>();

        CreateMap<NhDivisionMutateModel, NhDivision>().MapOnlyIfChanged();
        CreateMap<NhDivision, NhDivisionMutateModel>().MapOnlyIfChanged();

        CreateMap<NhDivisionUserMutateModel, NhDivisionUser>().MapOnlyIfChanged();
        CreateMap<NhDivisionUser, NhDivisionUserMutateModel>().MapOnlyIfChanged();
    }
}