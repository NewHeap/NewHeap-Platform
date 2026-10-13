using NewHeap.Platform.Mapping;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Services;
using SampleProjectManagement.Core.Models.Mutate;
using SampleProjectManagement.Core.Models.View;
using SampleProjectManagement.DAL.Entities;
using System.Linq.Expressions;

namespace SampleProjectManagement.Core.Services;

/// <summary>
/// Manages the people on a project. A member must belong to the project's division and appears
/// once per project. After every change the assistant refreshes the member snapshot of the
/// project's agent threads, so listings and notifications follow; access itself is always checked
/// live through <see cref="ProjectSubjectAccessResolver"/>.
/// </summary>
public class ProjectMemberService : BaseDbEntityService<ProjectMember, ProjectMemberMutateModel, ProjectMemberService>
{
    private readonly INhAiSubjectAccess? _subjectAccess;
    private readonly ILogger<ProjectMemberService> _logger;

    public ProjectMemberService(
        IRepository<ProjectMember> repository,
        NhDbLogService dbLogService,
        LogHelperService logHelperService,
        IMapper mapper,
        IStringLocalizer<ProjectMemberService> localizer,
        ValidationService validationService,
        ILogger<ProjectMemberService> logger,
        INhAiSubjectAccess? subjectAccess = null)
        : base(repository, dbLogService, logHelperService, mapper, localizer, validationService)
    {
        _logger = logger;
        _subjectAccess = subjectAccess;
    }

    public IQueryable<ProjectMember> GetCollectionQuery(ProjectMemberCollectionRequestModel requestModel)
    {
        var query = GetQueryWithUser();
        if (requestModel.ProjectId.HasValue)
        {
            query = query.Where(member => member.ProjectId == requestModel.ProjectId.Value);
        }

        return query;
    }

    public IQueryable<ProjectMember> GetQueryWithUser()
    {
        return _repository.GetAll().Include(member => member.User);
    }

    public override async Task<TaskResult<ProjectMember?>> CreateAsync(
        ProjectMemberMutateModel mutateModel,
        Guid? committedByUserId = null,
        Action<ProjectMember>? beforeSave = null,
        CancellationToken cancellationToken = default,
        BaseDbEntityServiceOperationOptions? options = null)
    {
        var result = await base.CreateAsync(mutateModel, committedByUserId, beforeSave, cancellationToken, options);
        if (result.Success)
        {
            await RefreshSubjectAsync(result.Data.ProjectId, cancellationToken);
        }

        return result;
    }

    public override async Task<TaskResult<ProjectMember?>> UpdateAsync(
        Guid id,
        ProjectMemberMutateModel mutateModel,
        Guid? committedByUserId = null,
        Action<ProjectMember>? beforeSave = null,
        CancellationToken cancellationToken = default,
        BaseDbEntityServiceOperationOptions? options = null)
    {
        var previousProjectId = await GetProjectIdAsync(id, cancellationToken);
        var result = await base.UpdateAsync(id, mutateModel, committedByUserId, beforeSave, cancellationToken, options);
        if (result.Success)
        {
            await RefreshSubjectAsync(result.Data.ProjectId, cancellationToken);
            if (previousProjectId is { } previous && previous != result.Data.ProjectId)
            {
                await RefreshSubjectAsync(previous, cancellationToken);
            }
        }

        return result;
    }

    public override async Task<TaskResult<ProjectMember?>> DeleteAsync(
        Guid id,
        Guid? committedByUserId = null,
        CancellationToken cancellationToken = default,
        BaseDbEntityServiceOperationOptions? options = null)
    {
        var projectId = await GetProjectIdAsync(id, cancellationToken);
        var result = await base.DeleteAsync(id, committedByUserId, cancellationToken, options);
        if (result.Success && projectId is { } deletedFrom)
        {
            await RefreshSubjectAsync(deletedFrom, cancellationToken);
        }

        return result;
    }

    protected override async Task ValidateCreateUpdateDeleteAsync(
        CreateUpdateDeleteValidateModel<ProjectMember, ProjectMember, ProjectMemberMutateModel> model,
        CancellationToken cancellationToken = default)
    {
        await base.ValidateCreateUpdateDeleteAsync(model, cancellationToken);
        if (!model.TaskResult.Success || model.ActionType == CRUDActionType.Delete)
        {
            return;
        }

        var mutateModel = model.MutateModel!;
        if (!Enum.IsDefined(mutateModel.Role))
        {
            model.TaskResult.AddError(nameof(ProjectMemberMutateModel.Role), "The selected role does not exist.");
            return;
        }

        var divisionId = await _repository.GetDbSet<Project>()
            .Where(project => project.Id == mutateModel.ProjectId)
            .Select(project => (Guid?)project.DivisionId)
            .SingleOrDefaultAsync(cancellationToken);
        if (divisionId is null)
        {
            model.TaskResult.AddError(nameof(ProjectMemberMutateModel.ProjectId), "The selected project does not exist.");
            return;
        }

        var inDivision = await _repository.GetDbSet<NhDivisionUser>()
            .AnyAsync(
                divisionUser => divisionUser.UserId == mutateModel.UserId && divisionUser.DivisionId == divisionId.Value,
                cancellationToken);
        if (!inDivision)
        {
            model.TaskResult.AddError(
                nameof(ProjectMemberMutateModel.UserId),
                "A project member must belong to the project's division.");
            return;
        }

        var alreadyMember = await _repository.GetAll()
            .Where(member => member.ProjectId == mutateModel.ProjectId && member.UserId == mutateModel.UserId)
            .Where(member => model.SourceModel == null || member.Id != model.SourceModel.Id)
            .AnyAsync(cancellationToken);
        if (alreadyMember)
        {
            model.TaskResult.AddError(nameof(ProjectMemberMutateModel.UserId), "This person is already on the project.");
        }
    }

    protected override Task<IEnumerable<ChangedValue>> OnUpdateGetChangedProperties(
        ProjectMember? original,
        ProjectMember? updated,
        CancellationToken cancellationToken = default)
    {
        return _logHelper.ChangedProperties(
            original,
            updated,
            new Dictionary<Expression<Func<ProjectMember?, object?>>, Func<object?, Task<string?>>>(),
            x => x!.ProjectId,
            x => x!.UserId,
            x => x!.Role);
    }

    private Task<Guid?> GetProjectIdAsync(Guid id, CancellationToken cancellationToken)
    {
        return _repository.GetAll()
            .AsNoTracking()
            .Where(member => member.Id == id)
            .Select(member => (Guid?)member.ProjectId)
            .SingleOrDefaultAsync(cancellationToken);
    }

    private async Task RefreshSubjectAsync(Guid projectId, CancellationToken cancellationToken)
    {
        if (_subjectAccess is null)
        {
            return;
        }

        var refreshed = await _subjectAccess.RefreshAsync(ProjectSubjects.For(projectId), cancellationToken);
        if (!refreshed.Success)
        {
            // The snapshot feeds listings and notifications only; the next refresh repairs it.
            _logger.LogWarning("The agent thread members of project {ProjectId} were not refreshed.", projectId);
        }
    }
}
