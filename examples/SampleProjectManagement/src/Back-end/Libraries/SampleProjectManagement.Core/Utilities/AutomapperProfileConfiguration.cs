using NewHeap.Platform.Mapping;
using NewHeap.Platform.Common;
using SampleProjectManagement.Core.Models.Mutate;
using SampleProjectManagement.Core.Models.View;
using SampleProjectManagement.DAL.Entities;

namespace SampleProjectManagement.Core.Utilities;

public class AutomapperProfileConfiguration : Profile
{
    public AutomapperProfileConfiguration()
    {
        CreateMap<Project, ProjectViewModel>();
        CreateMap<Project, ProjectCompositeViewModel>()
            .ForMember(destination => destination.Project, options => options.MapFrom(source => source))
            .ForMember(destination => destination.Tasks, options => options.MapFrom(source => source.Tasks));
        CreateMap<ProjectMutateModel, Project>().MapOnlyIfChanged();
        CreateMap<Project, ProjectMutateModel>().MapOnlyIfChanged();

        CreateMap<ProjectTask, ProjectTaskViewModel>();
        CreateMap<ProjectTaskMutateModel, ProjectTask>().MapOnlyIfChanged();
        CreateMap<ProjectTask, ProjectTaskMutateModel>().MapOnlyIfChanged();
    }
}
