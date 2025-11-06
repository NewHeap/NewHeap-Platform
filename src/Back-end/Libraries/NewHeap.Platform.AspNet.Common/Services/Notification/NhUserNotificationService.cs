using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models.Mutate;
using NewHeap.Platform.AspNet.Common.Models.View;
using NewHeap.Platform.Common;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Services;
using System.Linq.Expressions;

namespace NewHeap.Platform.AspNet.Common.Services.Notification;

public interface INhUserNotificationService : IBaseDbEntityService<NhUserNotification, NhUserNotificationMutateModel>
{
    Task<TaskResult> AddMessageAsync(Guid id, NhAddMessageUserNotificationMutateModel mutateModel, CancellationToken cancellationToken = default);
    Task<NhOverviewUserNotificationViewModel> GetOverviewByUserIdAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<TaskResult> MarkAllIsLastReadByUserIdAsync(Guid userId, bool isLastRead, CancellationToken cancellationToken = default);
    Task<TaskResult> MarkIsLastReadAsync(Guid id, bool isLastRead, CancellationToken cancellationToken = default);
    Task<TaskResult> ArchiveAsync(Guid id, bool isArchived, CancellationToken cancellationToken = default);
    Task<TaskResult> ArchiveAllByUserIdAsync(Guid userId, bool isArchived, CancellationToken cancellationToken = default);
}

public class NhUserNotificationService : BaseDbEntityService<NhUserNotification, NhUserNotificationMutateModel, NhUserNotificationService>, INhUserNotificationService
{
    protected readonly ILogger _logger;

    public NhUserNotificationService(
        IRepository<NhUserNotification> repository,
        IStringLocalizer<NhUserNotificationService> localizer,
        INhDbLogService dbLogService,
        LogHelperService logHelperService,
        ValidationService validationService,
        IMapper mapper,
        ILogger<NhNotificationService> logger
        )
        : base(repository, dbLogService, logHelperService, mapper, localizer, validationService)
    {
        _logger = logger;
    }

    protected override async Task<IEnumerable<ChangedValue>> OnUpdateGetChangedProperties(NhUserNotification? original,
        NhUserNotification? changed,
        CancellationToken cancellationToken = default)
    {
        return await _logHelper.ChangedProperties(original, changed,
            new Dictionary<Expression<Func<NhUserNotification?, object?>>, Func<object?, Task<string?>>>
            {
                // Method resolvers
            },
            x => x!.Id
        );
    }

    public override IQueryable<NhUserNotification> QueryableWithAllIncludes(IQueryable<NhUserNotification>? queryable = null)
    {
        return base.QueryableWithAllIncludes(queryable);
    }

    public override IQueryable<NhUserNotification> QueryableWithUpdateDeleteIncludes(IQueryable<NhUserNotification>? queryable = null)
    {
        return base.QueryableWithUpdateDeleteIncludes(queryable);
    }

    public override Task<NhUserNotification?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return DoGetAsync(id, cancellationToken);
    }

    protected override async Task ValidateCreateUpdateDeleteAsync(CreateUpdateDeleteValidateModel<NhUserNotification, NhUserNotification, NhUserNotificationMutateModel> model, CancellationToken cancellationToken = default)
    {
        if (!model.TaskResult.Success)
        {
            return;
        }

        if (model.ActionType == CRUDActionType.Create)
        {
            await CreateUpdateCheck();
        }
        else if (model.ActionType == CRUDActionType.Update)
        {
            if (model.TaskResult.Success)
            {
                await CreateUpdateCheck();
            }
        }
        else if (model.ActionType == CRUDActionType.Delete)
        {
        }

        return;

        async Task CreateUpdateCheck()
        {
            _validationService.ValidateMutateModelModelState(model);

            if (!model.TaskResult.Success)
            {
                return;
            }
        }
    }

    public override async Task<TaskResult<NhUserNotification?>> CreateAsync(
        NhUserNotificationMutateModel mutateModel,
        Guid? committedByUserId = null, Action<NhUserNotification>? beforeSave = null,
        CancellationToken cancellationToken = default,
        BaseDbEntityServiceOperationOptions? options = null
        )
    {
        var taskResult = new TaskResult<NhUserNotification?>();

        await DoValidateCreateAsync(
            new CreateUpdateDeleteValidateModel<NhUserNotification, NhUserNotification, NhUserNotificationMutateModel>(CRUDActionType.Create)
            {
                MutateModel = mutateModel,
                TaskResult = taskResult!
            }, cancellationToken);

        if (!taskResult.Success)
        {
            return taskResult;
        }

        var myBeforeSave = (NhUserNotification x) =>
        {
            x.LastTitle = mutateModel.Title!;
            x.LastMessage = mutateModel.Message ?? "";
            x.UserId = mutateModel!.UserId!.Value;
            x.CreationDateTime = DateTimeOffset.UtcNow;
            x.LastModifiedDateTime = DateTimeOffset.UtcNow;
            x.IsLastRead = false;
            x.Data.Url = mutateModel.Url;
            x.Data.UrlInNewTab = mutateModel.UrlInNewTab;

            if (!string.IsNullOrWhiteSpace(mutateModel.Message))
            {
                x.Messages.Add(new NhUserNotificationMessage()
                {
                    Title = mutateModel.Title!,
                    Message = mutateModel.Message,
                    UserNotification = x
                });
            }

            beforeSave?.Invoke(x);
        };

        using var transaction = await _repository.StartOrGetTransactionScopeAsync(cancellationToken);

        try
        {
            var baseResult = await DoCreateAsync(mutateModel, committedByUserId, myBeforeSave,
                cancellationToken: cancellationToken);

            if (!baseResult.Success)
            {
                return baseResult.ApplyToTaskResult(taskResult);
            }

            taskResult.Data = baseResult.Data;

            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while creating the notification: {Message}", ex.Message);
            taskResult.AddError(string.Empty, _localizer["An error occurred while creating the notification."]);
            return taskResult;
        }

        return taskResult;
    }

    public override async Task<TaskResult<NhUserNotification?>> UpdateAsync(
        Guid id,
        NhUserNotificationMutateModel mutateModel,
        Guid? committedByUserId = null, Action<NhUserNotification>? beforeSave = null,
        CancellationToken cancellationToken = default,
        BaseDbEntityServiceOperationOptions? options = null)
    {
        var taskResult = new TaskResult<NhUserNotification?>();

        await DoValidateCreateAsync(
            new CreateUpdateDeleteValidateModel<NhUserNotification, NhUserNotification, NhUserNotificationMutateModel>(CRUDActionType.Create)
            {
                MutateModel = mutateModel,
                TaskResult = taskResult
            }, cancellationToken);

        if (!taskResult.Success)
        {
            return taskResult;
        }

        var myBeforeSave = (NhUserNotification x) =>
        {
            x.LastTitle = mutateModel.Title!;
            x.LastMessage = mutateModel.Message ?? "";
            x.LastModifiedDateTime = DateTimeOffset.UtcNow;
            x.IsLastRead = false;

            if (!string.IsNullOrWhiteSpace(mutateModel.Message))
            {
                x.Messages.Add(new NhUserNotificationMessage()
                {
                    Title = mutateModel.Title!,
                    Message = mutateModel.Message,
                    UserNotification = x
                });
            }

            beforeSave?.Invoke(x);
        };

        using var transaction = await _repository.StartOrGetTransactionScopeAsync(cancellationToken);

        try
        {
            var baseResult = await DoUpdateAsync(id, mutateModel, committedByUserId, myBeforeSave,
                cancellationToken: cancellationToken);

            if (!baseResult.Success)
            {
                return baseResult.ApplyToTaskResult(taskResult);
            }

            taskResult.Data = baseResult.Data;

            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while updating the notification: {Message}", ex.Message);
            taskResult.AddError(string.Empty, _localizer["An error occurred while creating the notification."]);
            return taskResult;
        }

        return taskResult;
    }

    public async Task<TaskResult> AddMessageAsync(Guid id, NhAddMessageUserNotificationMutateModel mutateModel, CancellationToken cancellationToken = default)
    {
        var taskResult = new TaskResult();

        _validationService
            .ValidateMutateModelModelState(mutateModel)
            .ApplyToTaskResult(taskResult);

        if (!taskResult.Success)
        {
            return taskResult;
        }

        var userNotification = await _repository
            .GetAll()
            .Where(x => x.Id == id)
            .AsSplitQuery()
            .FirstOrDefaultAsync(cancellationToken);

        if (userNotification == null)
        {
            taskResult.AddError(nameof(id), "Notification not found");
        }

        var newMessage = new NhUserNotificationMessage()
        {
            Title = mutateModel.Title!,
            Message = mutateModel.Message ?? "",
            UserNotification = userNotification
        };

        userNotification!.Messages.Add(newMessage);

        userNotification.LastTitle = newMessage.Title;
        userNotification.LastMessage = newMessage.Message;
        userNotification.LastModifiedDateTime = DateTimeOffset.UtcNow;
        userNotification.IsLastRead = false;

        await _repository.SaveChangesAsync(cancellationToken);

        return taskResult;
    }

    public async Task<TaskResult> MarkIsLastReadAsync(Guid id, bool isLastRead, CancellationToken cancellationToken = default)
    {
        var result = new TaskResult();

        var userNotification = await _repository
            .GetAll()
            .Where(x => x.Id == id)
            .AsSplitQuery()
            .FirstOrDefaultAsync(cancellationToken);

        if (userNotification == null)
        {
            result.AddError(nameof(id), "Notification not found");
        }

        if (!result.Success)
        {
            return result;
        }

        userNotification!.IsLastRead = isLastRead;
        userNotification.LastModifiedDateTime = DateTimeOffset.UtcNow;
        await _repository.SaveChangesAsync(cancellationToken);

        return result;
    }

    public async Task<TaskResult> MarkAllIsLastReadByUserIdAsync(Guid userId, bool isLastRead, CancellationToken cancellationToken = default)
    {
        var result = new TaskResult();

        var userNotifications = await _repository
            .GetAll()
            .Where(x => x.UserId == userId && x.IsLastRead != isLastRead)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

        foreach (var userNotification in userNotifications)
        {
            userNotification.IsLastRead = isLastRead;
            userNotification.LastModifiedDateTime = DateTimeOffset.UtcNow;
        }

        await _repository.SaveChangesAsync(cancellationToken);

        return result;
    }

    public async Task<NhOverviewUserNotificationViewModel> GetOverviewByUserIdAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var overviewInfo = await _repository
            .Context
            .Database
            .SqlQuery<NhOverviewUserNotificationViewModel>(@$"
                SELECT ISNULL(COUNT(*), 0) AS TotalCount, 
                       ISNULL(SUM(CASE WHEN IsLastRead = 0 THEN 1 ELSE 0 END), 0) AS UnreadCount, 
                       MAX(LastModifiedDateTime) AS LastNotificationDate
                FROM UserNotifications
                WHERE UserId = {userId}
            ")
            .FirstOrDefaultAsync(cancellationToken);

        return overviewInfo ?? new NhOverviewUserNotificationViewModel();
    }

    public async Task<TaskResult> ArchiveAsync(Guid id, bool isArchived, CancellationToken cancellationToken = default)
    {
        var result = new TaskResult();

        var userNotification = await _repository
            .GetAll()
            .Where(x => x.Id == id)
            .AsSplitQuery()
            .FirstOrDefaultAsync(cancellationToken);

        if (userNotification == null)
        {
            result.AddError(nameof(id), "Notification not found");
        }

        if (!result.Success)
        {
            return result;
        }

        userNotification!.IsArchived = isArchived;
        userNotification.LastModifiedDateTime = DateTimeOffset.UtcNow;
        await _repository.SaveChangesAsync(cancellationToken);

        return result;
    }

    public async Task<TaskResult> ArchiveAllByUserIdAsync(Guid userId, bool isArchived, CancellationToken cancellationToken = default)
    {
        var result = new TaskResult();

        var userNotifications = await _repository
            .GetAll()
            .Where(x => x.UserId == userId && x.IsArchived != isArchived)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

        foreach (var userNotification in userNotifications)
        {
            userNotification.IsArchived = isArchived;
            userNotification.LastModifiedDateTime = DateTimeOffset.UtcNow;
        }

        await _repository.SaveChangesAsync(cancellationToken);

        return result;
    }
}