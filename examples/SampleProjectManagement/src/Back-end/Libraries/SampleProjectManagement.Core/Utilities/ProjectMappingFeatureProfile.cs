using NewHeap.Platform.Mapping;
using SampleProjectManagement.Core.Models.View;
using SampleProjectManagement.DAL.Entities;

namespace SampleProjectManagement.Core.Utilities;

public sealed class ProjectMappingFeatureProfile : Profile
{
    public ProjectMappingFeatureProfile()
    {
        CreateMap<Project, ProjectMappingSummaryViewModel>()
            .ConstructUsing(source => new ProjectMappingSummaryViewModel(source.Key))
            .ForMember(
                destination => destination.DisplayName,
                options => options.MapFrom<ProjectDisplayNameResolver>())
            .ForMember(
                destination => destination.OwnerUser,
                options => options.MapFrom(source => source.OwnerUser!.UserName))
            .ForMember(destination => destination.EnrichedBy, options => options.Ignore())
            .AfterMap<ProjectMappingEnrichmentAction>();

        CreateMap<Project, ProjectReferenceValue>()
            .ConvertUsing<ProjectReferenceConverter>();

        CreateMap<Project, ProjectMappingBaseViewModel>()
            .ForMember(
                destination => destination.DisplayName,
                options => options.MapFrom(source => $"{source.Key} · {source.Name}"))
            .ForMember(
                destination => destination.Metadata,
                options => options.MapFrom(source => ProjectMappingMetadata.Create(source)));

        CreateMap<Project, ProjectMappingDetailViewModel>()
            .IncludeBase<Project, ProjectMappingBaseViewModel>();
    }
}

public static class ProjectMappingMetadata
{
    public static Dictionary<string, string> Create(Project project)
    {
        return new Dictionary<string, string>
        {
            ["status"] = project.Status.ToString()
        };
    }
}

public sealed class ProjectMappingLabelFormatter
{
    public string DisplayName(Project project)
        => $"{project.Key} · {project.Name}";

    public string Reference(Project project)
        => $"{project.Key}:{project.Id:N}";
}

public sealed class ProjectDisplayNameResolver(ProjectMappingLabelFormatter formatter) :
    IValueResolver<Project, ProjectMappingSummaryViewModel, string>
{
    public string Resolve(
        Project source,
        ProjectMappingSummaryViewModel destination,
        string destinationMember,
        ResolutionContext context)
        => formatter.DisplayName(source);
}

public sealed class ProjectReferenceConverter(ProjectMappingLabelFormatter formatter) :
    ITypeConverter<Project, ProjectReferenceValue>
{
    public ProjectReferenceValue Convert(
        Project source,
        ProjectReferenceValue destination,
        ResolutionContext context)
        => new(formatter.Reference(source));
}

public sealed class ProjectMappingEnrichmentAction :
    IMappingAction<Project, ProjectMappingSummaryViewModel>
{
    public void Process(
        Project source,
        ProjectMappingSummaryViewModel destination,
        ResolutionContext context)
    {
        destination.EnrichedBy = nameof(ProjectMappingEnrichmentAction);
    }
}
