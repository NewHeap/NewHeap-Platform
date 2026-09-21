using NewHeap.Platform.Mapping;
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
using System.Security.Cryptography;
using System.Text;

namespace NewHeap.Platform.AspNet.Common.Services.Notification;

public interface INhUserNotificationService : IBaseDbEntityService<NhUserNotification, NhUserNotificationMutateModel>
{
    Task<TaskResult> AddMessageAsync(Guid id, NhAddMessageUserNotificationMutateModel mutateModel, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the user's most recently changed non-archived notification with the
    /// given thread key, or <see langword="null"/> when there is none.
    /// </summary>
    Task<NhUserNotification?> GetActiveByGroupKeyAsync(Guid userId, string groupKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends a message to the user's active notification with the same
    /// <see cref="NhUserNotificationMutateModel.GroupKey"/>, or creates a new
    /// notification when there is no key or no active thread. Concurrent calls for
    /// the same user and key are serialized.
    /// </summary>
    Task<TaskResult<NhUserNotification?>> CreateOrAddMessageAsync(NhUserNotificationMutateModel mutateModel, CancellationToken cancellationToken = default);

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
            x.Category = NormalizeOptional(mutateModel.Category);
            x.Severity = mutateModel.Severity;
            x.GroupKey = NormalizeOptional(mutateModel.GroupKey);
            x.Data.Url = mutateModel.Url;
            x.Data.UrlInNewTab = mutateModel.UrlInNewTab;

            if (!string.IsNullOrWhiteSpace(mutateModel.Message))
            {
                x.Messages.Add(new NhUserNotificationMessage()
                {
                    Title = mutateModel.Title!,
                    Message = mutateModel.Message,
                    Severity = mutateModel.Severity,
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
            _logger.LogError(ex, "An error occurred while creating the notification.");
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
            x.Category = NormalizeOptional(mutateModel.Category);
            x.Severity = mutateModel.Severity;
            x.GroupKey = NormalizeOptional(mutateModel.GroupKey);

            if (!string.IsNullOrWhiteSpace(mutateModel.Message))
            {
                x.Messages.Add(new NhUserNotificationMessage()
                {
                    Title = mutateModel.Title!,
                    Message = mutateModel.Message,
                    Severity = mutateModel.Severity,
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
            _logger.LogError(ex, "An error occurred while updating the notification.");
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
            return taskResult;
        }

        var newMessage = new NhUserNotificationMessage()
        {
            Title = mutateModel.Title!,
            Message = mutateModel.Message ?? "",
            Severity = mutateModel.Severity,
            UserNotification = userNotification
        };

        userNotification.Messages.Add(newMessage);

        userNotification.LastTitle = newMessage.Title;
        userNotification.LastMessage = newMessage.Message;
        userNotification.Severity = newMessage.Severity;
        userNotification.LastModifiedDateTime = DateTimeOffset.UtcNow;
        userNotification.IsLastRead = false;

        if (!string.IsNullOrWhiteSpace(mutateModel.Url)
            && !string.Equals(userNotification.Data.Url, mutateModel.Url, StringComparison.Ordinal))
        {
            // Data is stored through a value conversion; assign a new instance so
            // the change tracker detects the new link.
            userNotification.Data = new NhUserNotficationData
            {
                Url = mutateModel.Url,
                UrlInNewTab = userNotification.Data.UrlInNewTab
            };
        }

        await _repository.SaveChangesAsync(cancellationToken);

        return taskResult;
    }

    public async Task<NhUserNotification?> GetActiveByGroupKeyAsync(Guid userId, string groupKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupKey);

        var normalizedGroupKey = groupKey.Trim();

        return await _repository
            .GetAll()
            .Where(x => x.UserId == userId && x.GroupKey == normalizedGroupKey && !x.IsArchived)
            .OrderByDescending(x => x.LastModifiedDateTime)
            .ThenByDescending(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<TaskResult<NhUserNotification?>> CreateOrAddMessageAsync(
        NhUserNotificationMutateModel mutateModel,
        CancellationToken cancellationToken = default)
    {
        var groupKey = NormalizeOptional(mutateModel.GroupKey);
        if (groupKey is null || !mutateModel.UserId.HasValue)
        {
            return await CreateAsync(mutateModel, cancellationToken: cancellationToken);
        }

        var taskResult = new TaskResult<NhUserNotification?>();

        await using var transaction = await _repository.StartOrGetTransactionScopeAsync(cancellationToken);
        if (!await _repository.TryAcquireTransactionLockAsync(
                transaction,
                GetGroupLockName(mutateModel.UserId.Value, groupKey),
                GroupLockTimeoutMilliseconds,
                cancellationToken))
        {
            return taskResult.WithError(string.Empty, "notification-group-busy");
        }

        var existing = await GetActiveByGroupKeyAsync(mutateModel.UserId.Value, groupKey, cancellationToken);
        if (existing is null)
        {
            var createResult = await CreateAsync(mutateModel, cancellationToken: cancellationToken);
            if (createResult.Success)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return createResult;
        }

        var addResult = await AddMessageAsync(
            existing.Id,
            new NhAddMessageUserNotificationMutateModel
            {
                Title = mutateModel.Title,
                Message = mutateModel.Message,
                Severity = mutateModel.Severity,
                Url = mutateModel.Url
            },
            cancellationToken);
        if (!addResult.Success)
        {
            return TaskResult<NhUserNotification?>.Failed(addResult);
        }

        await transaction.CommitAsync(cancellationToken);
        taskResult.Data = existing;
        return taskResult;
    }

    private const int GroupLockTimeoutMilliseconds = 5_000;

    private static string GetGroupLockName(Guid userId, string groupKey)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(groupKey)));
        return $"NhUserNotification:Group:{userId:N}:{hash}";
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
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
        var overviewRows = await _repository
            .GetAll()
            .Where(x => x.UserId == userId && !x.IsArchived)
            .GroupBy(_ => 1)
            .Select(notifications => new NhOverviewUserNotificationViewModel
            {
                TotalCount = notifications.Count(),
                UnreadCount = notifications.Count(x => !x.IsLastRead),
                LastNotificationDate = notifications.Max(x => x.LastModifiedDateTime)
            })
            .ToListAsync(cancellationToken);

        var overviewInfo = overviewRows.SingleOrDefault();

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
