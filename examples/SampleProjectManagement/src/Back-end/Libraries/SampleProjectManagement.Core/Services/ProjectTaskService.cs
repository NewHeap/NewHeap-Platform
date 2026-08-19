using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common;
using NewHeap.Platform.Common.Events;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Services;
using SampleProjectManagement.Core.Events;
using SampleProjectManagement.Core.Models.Mutate;
using SampleProjectManagement.Core.Models.View;
using SampleProjectManagement.DAL.Entities;
using System.Linq.Expressions;

namespace SampleProjectManagement.Core.Services;

public class ProjectTaskService : BaseDbEntityService<ProjectTask, ProjectTaskMutateModel, ProjectTaskService>
{
    private readonly INhEventPublisher _eventPublisher;

    public ProjectTaskService(
        IRepository<ProjectTask> repository,
        NhDbLogService dbLogService,
        LogHelperService logHelperService,
        IMapper mapper,
        IStringLocalizer<ProjectTaskService> localizer,
        ValidationService validationService,
        INhEventPublisher eventPublisher)
        : base(repository, dbLogService, logHelperService, mapper, localizer, validationService)
    {
        _eventPublisher = eventPublisher;
    }

    public IQueryable<ProjectTask> GetCollectionQuery(ProjectTaskCollectionRequestModel requestModel)
    {
        var query = _repository.GetAll();
        if (requestModel.ProjectId.HasValue)
        {
            query = query.Where(task => task.ProjectId == requestModel.ProjectId.Value);
        }

        return query;
    }

    public override async Task<TaskResult<ProjectTask?>> CreateAsync(
        ProjectTaskMutateModel mutateModel,
        Guid? committedByUserId = null,
        Action<ProjectTask>? beforeSave = null,
        CancellationToken cancellationToken = default,
        BaseDbEntityServiceOperationOptions? options = null)
    {
        Normalize(mutateModel);
        await using var transaction = await _repository.StartOrGetTransactionScopeAsync(cancellationToken);
        var result = await base.CreateAsync(
            mutateModel,
            committedByUserId,
            beforeSave,
            cancellationToken,
            options);
        if (!result.Success)
        {
            return result;
        }

        await _eventPublisher.PublishAsync(new ProjectTaskCreatedEvent
        {
            ProjectTaskId = result.Data.Id,
            ProjectId = result.Data.ProjectId
        });
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public override async Task<TaskResult<ProjectTask?>> UpdateAsync(
        Guid id,
        ProjectTaskMutateModel mutateModel,
        Guid? committedByUserId = null,
        Action<ProjectTask>? beforeSave = null,
        CancellationToken cancellationToken = default,
        BaseDbEntityServiceOperationOptions? options = null)
    {
        Normalize(mutateModel);
        await using var transaction = await _repository.StartOrGetTransactionScopeAsync(cancellationToken);
        var result = await base.UpdateAsync(
            id,
            mutateModel,
            committedByUserId,
            beforeSave,
            cancellationToken,
            options);
        if (!result.Success)
        {
            return result;
        }

        await _eventPublisher.PublishAsync(new ProjectTaskUpdatedEvent
        {
            ProjectTaskId = result.Data.Id,
            ProjectId = result.Data.ProjectId
        });
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public override async Task<TaskResult<ProjectTask?>> DeleteAsync(
        Guid id,
        Guid? committedByUserId = null,
        CancellationToken cancellationToken = default,
        BaseDbEntityServiceOperationOptions? options = null)
    {
        await using var transaction = await _repository.StartOrGetTransactionScopeAsync(cancellationToken);
        var projectId = await _repository.GetAll()
            .AsNoTracking()
            .Where(task => task.Id == id)
            .Select(task => (Guid?)task.ProjectId)
            .SingleOrDefaultAsync(cancellationToken);

        var result = await base.DeleteAsync(id, committedByUserId, cancellationToken, options);
        if (!result.Success)
        {
            return result;
        }

        await _eventPublisher.PublishAsync(new ProjectTaskDeletedEvent
        {
            ProjectTaskId = id,
            ProjectId = projectId ?? result.Data.ProjectId
        });
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    protected override async Task ValidateCreateUpdateDeleteAsync(
        CreateUpdateDeleteValidateModel<ProjectTask, ProjectTask, ProjectTaskMutateModel> model,
        CancellationToken cancellationToken = default)
    {
        await base.ValidateCreateUpdateDeleteAsync(model, cancellationToken);
        if (!model.TaskResult.Success || model.ActionType == CRUDActionType.Delete)
        {
            return;
        }

        var projectExists = await _repository.GetDbSet<Project>()
            .AnyAsync(project => project.Id == model.MutateModel!.ProjectId, cancellationToken);
        if (!projectExists)
        {
            model.TaskResult.AddError(
                nameof(ProjectTaskMutateModel.ProjectId),
                "The selected project does not exist.");
        }
    }

    protected override Task<IEnumerable<ChangedValue>> OnUpdateGetChangedProperties(
        ProjectTask? original,
        ProjectTask? updated,
        CancellationToken cancellationToken = default)
    {
        return _logHelper.ChangedProperties(
            original,
            updated,
            new Dictionary<Expression<Func<ProjectTask?, object?>>, Func<object?, Task<string?>>>(),
            x => x!.ProjectId,
            x => x!.Title,
            x => x!.IsCompleted);
    }

    private static void Normalize(ProjectTaskMutateModel mutateModel)
    {
        mutateModel.Title = mutateModel.Title.Trim();
    }
}
