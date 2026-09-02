using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models.Mutate;
using NewHeap.Platform.AspNet.Common.Models.View;
using NewHeap.Platform.Common;
using System.Security.Claims;

namespace NewHeap.Platform.AspNet.Common.Utilities;

public class AutomapperProfileConfiguration : NewHeap.Platform.Mapping.Profile
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
        CreateMap<NhDivisionUser, DivisionUserViewModel<NhUserViewModel<NhDivisionViewModel>, NhDivisionViewModel, NhDivisionRoleViewModel>>()
            .ForMember(x => x.Roles, opts => opts.MapFrom(x => x.DivisionUserRoles.Select(c => new NhDivisionRoleViewModel { Id = c.DivisionRole.Id, Name = c.DivisionRole.Name }).ToList()))
        ;
        CreateMap<NhDivisionRole, NhDivisionRoleViewModel>();

        CreateMap<NhDivisionMutateModel, NhDivision>().MapOnlyIfChanged();
        CreateMap<NhDivision, NhDivisionMutateModel>().MapOnlyIfChanged();

        CreateMap<NhDivisionUserMutateModel, NhDivisionUser>().MapOnlyIfChanged();
        CreateMap<NhDivisionUser, NhDivisionUserMutateModel>().MapOnlyIfChanged();

        CreateMap<NhUserNotification, NhUserNotificationViewModel>();
        CreateMap<NhUserNotificationMutateModel, NhUserNotification>().MapOnlyIfChanged();
        CreateMap<NhUserNotification, NhUserNotificationMutateModel>().MapOnlyIfChanged();

        CreateMap<NhBackgroundOperation, NhBackgroundOperationViewModel>();
        CreateMap<NhBackgroundOperation, NhBackgroundOperationChildViewModel>();
        CreateMap<NhBackgroundOperationMutateModel, NhBackgroundOperation>().MapOnlyIfChanged();
        CreateMap<NhBackgroundOperation, NhBackgroundOperationMutateModel>().MapOnlyIfChanged();
        CreateMap<NhBackgroundOperationAttempt, NhBackgroundOperationAttemptViewModel>();
        CreateMap<NhBackgroundOperationStep, NhBackgroundOperationStepViewModel>();
        CreateMap<NhBackgroundOperationEvent, NhBackgroundOperationEventViewModel>();
    }
}
