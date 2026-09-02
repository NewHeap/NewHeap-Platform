using NewHeap.Platform.Mapping;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.Models;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Services;
using SampleProjectManagement.Core.Models.Mutate;
using SampleProjectManagement.DAL.Entities;

namespace SampleProjectManagement.Core.Services;

/// <summary>
/// Concrete composite adapter for the project aggregate. Business rules and
/// transaction/event orchestration remain in <see cref="ProjectService"/>;
/// this service supplies the composite library contract and aggregate query.
/// </summary>
public sealed class ProjectCompositeService
    : CompositeBaseDbEntityService<Project, ProjectMutateModel, Project, ProjectCompositeService>
{
    private readonly ProjectService _projectService;

    public ProjectCompositeService(
        IRepository<Project> repository,
        NhDbLogService dbLogService,
        LogHelperService logHelperService,
        IMapper mapper,
        IStringLocalizer<ProjectCompositeService> localizer,
        ValidationService validationService,
        ProjectService projectService)
        : base(repository, dbLogService, logHelperService, mapper, localizer, validationService)
    {
        _projectService = projectService;
    }

    public override Task<Project?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return _repository.GetAll()
            .Include(project => project.Tasks)
            .SingleOrDefaultAsync(project => project.Id == id, cancellationToken);
    }

    public override Task<TaskResult<Project?>> CreateAsync(
        ProjectMutateModel mutateModel,
        Guid? committedByUserId = null,
        Action<Project>? beforeSave = null,
        CancellationToken cancellationToken = default,
        CompositeBaseDbEntityServiceOperationOptions? options = null)
    {
        return _projectService.CreateAsync(
            mutateModel,
            committedByUserId,
            beforeSave,
            cancellationToken,
            ToBaseOptions(options));
    }

    public override Task<TaskResult<Project?>> UpdateAsync(
        Guid id,
        ProjectMutateModel mutateModel,
        Guid? committedByUserId = null,
        Action<Project>? beforeSave = null,
        CancellationToken cancellationToken = default,
        CompositeBaseDbEntityServiceOperationOptions? options = null)
    {
        return _projectService.UpdateAsync(
            id,
            mutateModel,
            committedByUserId,
            beforeSave,
            cancellationToken,
            ToBaseOptions(options));
    }

    public override Task<TaskResult<Project?>> DeleteAsync(
        Guid id,
        Guid? committedByUserId = null,
        CancellationToken cancellationToken = default,
        CompositeBaseDbEntityServiceOperationOptions? options = null)
    {
        return _projectService.DeleteAsync(
            id,
            committedByUserId,
            cancellationToken,
            ToBaseOptions(options));
    }

    protected override Task ValidateCreateUpdateDeleteAsync(
        CreateUpdateDeleteValidateModel<Project, Project, ProjectMutateModel> model,
        CancellationToken cancellationToken = default)
    {
        return DoValidateCreateUpdateDeleteAsync(model, cancellationToken);
    }

    private static BaseDbEntityServiceOperationOptions? ToBaseOptions(
        CompositeBaseDbEntityServiceOperationOptions? options)
    {
        return options is null
            ? null
            : new BaseDbEntityServiceOperationOptions
            {
                DbLoggingDisabled = options.DbLoggingDisabled,
                SaveChangesDisabled = options.SaveChangesDisabled
            };
    }
}
