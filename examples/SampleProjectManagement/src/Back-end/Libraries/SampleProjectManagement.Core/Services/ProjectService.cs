using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.AspNet.Services;
using NewHeap.Platform.Common;
using NewHeap.Platform.Common.Events;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Services;
using NewHeap.Platform.Common.Utilities;
using SampleProjectManagement.Core.Events;
using SampleProjectManagement.Core.Models.Mutate;
using SampleProjectManagement.Core.Models.View;
using SampleProjectManagement.DAL.Entities;
using System.Linq.Expressions;

namespace SampleProjectManagement.Core.Services;

public class ProjectService : BaseDbEntityService<Project, ProjectMutateModel, ProjectService>
{
    private static readonly NhProjectionDefinition<Project, ProjectProjectionViewModel> ProjectProjection =
        NhProjection
            .For<Project, ProjectProjectionViewModel>()
            .Map(destination => destination.DisplayName, source => source.Key + " — " + source.Name)
            .Map(destination => destination.OpenTaskCount, source => source.Tasks.Count(task => !task.IsCompleted))
            .IsSearchable(view => view.Key, view => view.Name, view => view.DisplayName)
            .IsFilterable(view => view.Id, view => view.OpenTaskCount)
            .IsOrderable(view => view.Key, view => view.Name, view => view.OpenTaskCount)
            .Build();

    private readonly INhEventPublisher _eventPublisher;

    public ProjectService(
        IRepository<Project> repository,
        NhDbLogService dbLogService,
        LogHelperService logHelperService,
        IMapper mapper,
        IStringLocalizer<ProjectService> localizer,
        ValidationService validationService,
        INhEventPublisher eventPublisher)
        : base(repository, dbLogService, logHelperService, mapper, localizer, validationService)
    {
        _eventPublisher = eventPublisher;
    }

    public IQueryable<Project> GetCollectionQuery(
        ProjectCollectionRequestModel requestModel,
        Guid? ownerUserId = null)
    {
        var query = _repository.GetAll();

        if (ownerUserId.HasValue)
        {
            query = query.Where(project => project.OwnerUserId == ownerUserId.Value);
        }

        if (requestModel.DivisionId.HasValue)
        {
            query = query.Where(project => project.DivisionId == requestModel.DivisionId.Value);
        }

        if (requestModel.Statuses.Count > 0)
        {
            query = query.Where(project => requestModel.Statuses.Contains(project.Status));
        }

        return query;
    }

    public async Task<ProjectCompositeViewModel?> GetCompositeAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var project = await _repository.GetAll()
            .Include(item => item.Tasks)
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);

        return project is null
            ? null
            : new ProjectCompositeViewModel
            {
                Project = _mapper.Map<ProjectViewModel>(project),
                Tasks = _mapper.Map<List<ProjectTaskViewModel>>(project.Tasks)
            };
    }

    public async Task<SimpleCollectionResultModel<ProjectShortViewModel>> GetShortAsync(
        CancellationToken cancellationToken = default)
    {
        var items = await _repository.GetAll()
            .OrderBy(item => item.Name)
            .Select(item => new ProjectShortViewModel
            {
                Id = item.Id,
                Key = item.Key,
                Name = item.Name
            })
            .ToListAsync(cancellationToken);

        return SimpleCollectionResultModel<ProjectShortViewModel>.Create(items);
    }

    public Task<List<ProjectProjectionViewModel>> GetProjectedAsync(
        CancellationToken cancellationToken = default)
    {
        return _repository.GetAll()
            .Select(ProjectProjection)
            .OrderBy(item => item.Name)
            .ToListAsync(cancellationToken);
    }

    public override async Task<TaskResult<Project?>> CreateAsync(
        ProjectMutateModel mutateModel,
        Guid? committedByUserId = null,
        Action<Project>? beforeSave = null,
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

        try
        {
            await _eventPublisher.PublishAsync(new ProjectCreatedEvent
            {
                ProjectId = result.Data.Id,
                ProjectKey = result.Data.Key
            });
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    /// <summary>
    /// SPM-200 deliberately saves and publishes without committing. Disposing
    /// the rolled-back scope proves that the SQL row and CAP outbox message
    /// share one transaction. This method exists only as an executable sample.
    /// </summary>
    public async Task<TaskResult<ProjectRollbackSampleViewModel>> CreateRolledBackSampleAsync(
        ProjectMutateModel mutateModel,
        Guid? committedByUserId = null,
        CancellationToken cancellationToken = default)
    {
        Normalize(mutateModel);
        await using var transaction = await _repository.StartOrGetTransactionScopeAsync(cancellationToken);
        var createResult = await base.CreateAsync(
            mutateModel,
            committedByUserId,
            cancellationToken: cancellationToken);
        var result = new TaskResult<ProjectRollbackSampleViewModel>();
        createResult.ApplyTo(result);
        if (!createResult.Success)
        {
            return result;
        }

        var @event = new ProjectCreatedEvent
        {
            ProjectId = createResult.Data.Id,
            ProjectKey = createResult.Data.Key
        };
        await _eventPublisher.PublishAsync(@event);

        // Deliberate sample rollback: no CommitAsync follows this publish.
        await transaction.RollbackAsync(cancellationToken);
        result.Data = new ProjectRollbackSampleViewModel
        {
            ProjectId = createResult.Data.Id,
            EventId = @event.EventId
        };
        return result;
    }

    public override async Task<TaskResult<Project?>> UpdateAsync(
        Guid id,
        ProjectMutateModel mutateModel,
        Guid? committedByUserId = null,
        Action<Project>? beforeSave = null,
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

        await _eventPublisher.PublishAsync(new ProjectUpdatedEvent
        {
            ProjectId = id,
            ChangeSet = "project"
        });
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public override async Task<TaskResult<Project?>> UpdatePartialAsync(
        Guid id,
        Func<NhSetPropertyCalls<ProjectMutateModel>, NhSetPropertyCalls<ProjectMutateModel>> set,
        Action<NhSetPropertyCalls<ProjectMutateModel>>? callsReady = null,
        Guid? committedByUserId = null,
        Action<Project>? beforeSave = null,
        CancellationToken cancellationToken = default,
        BaseDbEntityServiceOperationOptions? options = null)
    {
        await using var transaction = await _repository.StartOrGetTransactionScopeAsync(cancellationToken);
        var result = await base.UpdatePartialAsync(
            id,
            set,
            callsReady,
            committedByUserId,
            beforeSave,
            cancellationToken,
            options);
        if (!result.Success)
        {
            return result;
        }

        await _eventPublisher.PublishAsync(new ProjectUpdatedEvent
        {
            ProjectId = id,
            ChangeSet = "partial"
        });
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public override async Task<TaskResult<Project?>> DeleteAsync(
        Guid id,
        Guid? committedByUserId = null,
        CancellationToken cancellationToken = default,
        BaseDbEntityServiceOperationOptions? options = null)
    {
        await using var transaction = await _repository.StartOrGetTransactionScopeAsync(cancellationToken);
        var projectKey = await _repository.GetAll()
            .AsNoTracking()
            .Where(project => project.Id == id)
            .Select(project => project.Key)
            .SingleOrDefaultAsync(cancellationToken);

        var result = await base.DeleteAsync(id, committedByUserId, cancellationToken, options);
        if (!result.Success)
        {
            return result;
        }

        await _eventPublisher.PublishAsync(new ProjectDeletedEvent
        {
            ProjectId = id,
            ProjectKey = projectKey ?? ""
        });
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<TaskResult<Project?>> UpdatePlanningAsync(
        Guid id,
        ProjectPlanningMutateModel mutateModel,
        Guid? committedByUserId = null,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await _repository.StartOrGetTransactionScopeAsync(cancellationToken);
        var result = await base.UpdatePartialAsync(
            id,
            calls => calls
                .SetProperty(project => project.Deadline, mutateModel.Deadline)
                .SetProperty(project => project.Description, mutateModel.Description),
            committedByUserId: committedByUserId,
            cancellationToken: cancellationToken);
        if (!result.Success)
        {
            return result;
        }

        await _eventPublisher.PublishAsync(new ProjectUpdatedEvent
        {
            ProjectId = id,
            ChangeSet = "planning"
        });
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<TaskResult<Project?>> UpdateStatusAsync(
        Guid id,
        ProjectStatusMutateModel mutateModel,
        Guid? committedByUserId = null,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await _repository.StartOrGetTransactionScopeAsync(cancellationToken);
        var previousStatus = await _repository.GetAll()
            .AsNoTracking()
            .Where(project => project.Id == id)
            .Select(project => (ProjectStatus?)project.Status)
            .SingleOrDefaultAsync(cancellationToken);

        var result = await base.UpdatePartialAsync(
            id,
            calls => calls.SetProperty(project => project.Status, mutateModel.Status),
            committedByUserId: committedByUserId,
            cancellationToken: cancellationToken);
        if (!result.Success)
        {
            return result;
        }

        await _eventPublisher.PublishAsync(new ProjectStatusChangedEvent
        {
            ProjectId = id,
            PreviousStatus = previousStatus ?? result.Data.Status,
            Status = result.Data.Status
        });
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<TaskResult<ProjectBulkMutationResultViewModel>> BulkMutationsAsync(
        ProjectBulkMutationSampleModel mutateModel,
        Guid? committedByUserId = null,
        CancellationToken cancellationToken = default)
    {
        mutateModel.Creates.ForEach(Normalize);
        mutateModel.Updates.ForEach(item => Normalize(item.Model));

        await using var transaction = await _repository.StartOrGetTransactionScopeAsync(cancellationToken);
        var bulkResult = await base.BulkAsync(
            new BulkCRUDMutateModel<ProjectMutateModel, ProjectMutateModel, ProjectMutateModel>
            {
                // The application service owns the transaction so nested library operations join it.
                UseTransaction = false,
                ContinueOnError = mutateModel.ContinueOnError,
                Create = mutateModel.Creates,
                Update = mutateModel.Updates.Select(item => (item.Id, item.Model)).ToList(),
                Delete = mutateModel.Deletes
            },
            new BaseDbEntityServiceOperationOptions(),
            committedByUserId,
            cancellationToken: cancellationToken);

        var createResults = bulkResult.Data?.CreateResults?.ToList() ?? [];
        var updateResults = bulkResult.Data?.UpdateResults?.ToList() ?? [];
        var deleteResults = bulkResult.Data?.DeleteResults?.ToList() ?? [];
        var viewModel = new ProjectBulkMutationResultViewModel
        {
            Created = createResults.Count(item => item.Success),
            Updated = updateResults.Count(item => item.UpdateResult.Success),
            Deleted = deleteResults.Count(item => item.DeleteResult.Success),
            Failed = createResults.Count(item => !item.Success) +
                updateResults.Count(item => !item.UpdateResult.Success) +
                deleteResults.Count(item => !item.DeleteResult.Success)
        };
        var result = TaskResult<ProjectBulkMutationResultViewModel>.Succeeded(viewModel);
        bulkResult.ApplyTo(result);

        if (viewModel.Created + viewModel.Updated + viewModel.Deleted > 0 &&
            (bulkResult.Success || mutateModel.ContinueOnError))
        {
            await _eventPublisher.PublishAsync(new ProjectBulkChangedEvent
            {
                Created = viewModel.Created,
                Updated = viewModel.Updated,
                Deleted = viewModel.Deleted,
                Failed = viewModel.Failed
            });
            await transaction.CommitAsync(cancellationToken);
        }

        return result;
    }

    /// <summary>
    /// SPM-053 demonstrates the provider-native import path. The operation is immediate,
    /// bypasses EF change tracking, and upserts explicitly selected dependent tasks by primary key.
    /// </summary>
    public Task<int> ImportAsync(
        IReadOnlyCollection<Project> projects,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projects);

        var importDateTime = DateTimeOffset.UtcNow;
        foreach (var project in projects)
        {
            project.Key = project.Key.Trim().ToUpperInvariant();
            project.Name = project.Name.Trim();
            project.Description = string.IsNullOrWhiteSpace(project.Description)
                ? null
                : project.Description.Trim();
            if (project.CreationDateTime == default)
            {
                project.CreationDateTime = importDateTime;
            }

            project.LastModifiedDateTime = importDateTime;
            foreach (var task in project.Tasks)
            {
                task.Title = task.Title.Trim();
                if (task.CreationDateTime == default)
                {
                    task.CreationDateTime = importDateTime;
                }

                task.LastModifiedDateTime = importDateTime;
            }
        }

        return _repository.ExecuteUpsertAsync(
            projects,
            project => new { project.DivisionId, project.Key },
            [project => project.Tasks],
            cancellationToken);
    }

    public async Task<TaskResult<ProjectBulkStatusResultViewModel>> BulkUpdateStatusAsync(
        ProjectBulkStatusMutateModel mutateModel,
        Guid? committedByUserId = null,
        CancellationToken cancellationToken = default)
    {
        var partialUpdates = mutateModel.Ids
            .Distinct()
            .Select(id => (
                Id: id,
                Set: (Func<NhSetPropertyCalls<ProjectMutateModel>, NhSetPropertyCalls<ProjectMutateModel>>)
                    (calls => calls.SetProperty(project => project.Status, mutateModel.Status)),
                CallsReady: (Action<NhSetPropertyCalls<ProjectMutateModel>>?)null))
            .ToList();

        await using var transaction = await _repository.StartOrGetTransactionScopeAsync(cancellationToken);
        var bulkResult = await base.BulkAsync(
            new BulkCRUDMutateModel<ProjectMutateModel, ProjectMutateModel, ProjectMutateModel>
            {
                UseTransaction = false,
                ContinueOnError = mutateModel.ContinueOnError,
                UpdatePartial = partialUpdates
            },
            new BaseDbEntityServiceOperationOptions(),
            committedByUserId,
            cancellationToken: cancellationToken);

        var updateResults = bulkResult.Data?.UpdatePartialResults?.ToList() ?? [];
        var itemResults = updateResults.Select(item => new ProjectBulkStatusItemResultViewModel
        {
            Id = item.Id,
            Success = item.UpdatePartialResult.Success,
            ErrorMessages = item.UpdatePartialResult.AllErrorMessages
                .Select(error => error.ToString())
                .ToList()
        }).ToList();
        var viewModel = new ProjectBulkStatusResultViewModel
        {
            RequestedCount = partialUpdates.Count,
            Results = itemResults,
            SucceededCount = itemResults.Count(item => item.Success),
            FailedCount = itemResults.Count(item => !item.Success),
            FailedIds = itemResults
                .Where(item => !item.Success)
                .Select(item => item.Id)
                .ToList()
        };
        var result = TaskResult<ProjectBulkStatusResultViewModel>.Succeeded(viewModel);
        bulkResult.ApplyTo(result);

        if (viewModel.SucceededCount > 0 && (bulkResult.Success || mutateModel.ContinueOnError))
        {
            await _eventPublisher.PublishAsync(new ProjectBulkChangedEvent
            {
                Updated = viewModel.SucceededCount,
                Failed = viewModel.FailedCount
            });
            await transaction.CommitAsync(cancellationToken);
        }

        return result;
    }

    protected override async Task ValidateCreateUpdateDeleteAsync(
        CreateUpdateDeleteValidateModel<Project, Project, ProjectMutateModel> model,
        CancellationToken cancellationToken = default)
    {
        await base.ValidateCreateUpdateDeleteAsync(model, cancellationToken);
        if (!model.TaskResult.Success)
        {
            return;
        }

        if (model.ActionType is CRUDActionType.Create or CRUDActionType.Update)
        {
            var mutateModel = model.MutateModel!;
            var keyExists = await _repository.GetAll()
                .Where(project =>
                    project.DivisionId == mutateModel.DivisionId &&
                    project.Key == mutateModel.Key)
                .Where(project => model.SourceModel == null || project.Id != model.SourceModel.Id)
                .AnyAsync(cancellationToken);

            if (keyExists)
            {
                model.TaskResult.AddError(
                    nameof(ProjectMutateModel.Key),
                    "Project key must be unique within a division.");
            }
        }

        if (model.ActionType == CRUDActionType.Delete && model.SourceModel is not null)
        {
            var hasOpenTasks = await _repository
                .GetDbSet<ProjectTask>()
                .AnyAsync(
                    task => task.ProjectId == model.SourceModel.Id && !task.IsCompleted,
                    cancellationToken);
            if (hasOpenTasks)
            {
                model.TaskResult.AddError(
                    string.Empty,
                    "Complete the open tasks before deleting this project.");
            }
        }
    }

    protected override Task<IEnumerable<ChangedValue>> OnUpdateGetChangedProperties(
        Project? original,
        Project? updated,
        CancellationToken cancellationToken = default)
    {
        return _logHelper.ChangedProperties(
            original,
            updated,
            new Dictionary<Expression<Func<Project?, object?>>, Func<object?, Task<string?>>>(),
            x => x!.Key,
            x => x!.Name,
            x => x!.Description,
            x => x!.Status,
            x => x!.Deadline,
            x => x!.DivisionId);
    }

    protected override Task PreparePartialUpdateMutateModelAsync(
        ProjectMutateModel mutateModel,
        CancellationToken cancellationToken = default)
    {
        Normalize(mutateModel);
        return Task.CompletedTask;
    }

    private static void Normalize(ProjectMutateModel mutateModel)
    {
        mutateModel.Key = mutateModel.Key.Trim().ToUpperInvariant();
        mutateModel.Name = mutateModel.Name.Trim();
        mutateModel.Description = string.IsNullOrWhiteSpace(mutateModel.Description)
            ? null
            : mutateModel.Description.Trim();
    }
}
