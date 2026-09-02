using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models.Mutate;
using NewHeap.Platform.AspNet.Common.Services.Notification;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;

internal sealed class NhBackgroundOperationNotificationProjector : INhBackgroundOperationNotificationProjector
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly NhBackgroundOperationsOptions _options;

    public NhBackgroundOperationNotificationProjector(
        IServiceScopeFactory scopeFactory,
        NhBackgroundOperationsOptions options)
    {
        _scopeFactory = scopeFactory;
        _options = options;
    }

    public async Task<TaskResult> ProjectAsync(
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRepository<NhBackgroundOperation>>();
        var notificationService = scope.ServiceProvider.GetRequiredService<INhUserNotificationService>();
        var notificationFormatter = scope.ServiceProvider.GetRequiredService<INhBackgroundOperationNotificationFormatter>();
        await using var transaction = await repository.StartOrGetTransactionScopeAsync(cancellationToken);
        if (!await repository.TryAcquireTransactionLockAsync(
                transaction,
                $"NhBackgroundOperation:Operation:{operationId:N}",
                _options.TransactionLockTimeoutMilliseconds,
                cancellationToken))
        {
            return TaskResult.Failed(
                "notification-projection-busy",
                "background-operation.notification-projection-busy");
        }

        var operation = await repository.GetAll().SingleOrDefaultAsync(x => x.Id == operationId, cancellationToken);
        if (operation is null)
        {
            return TaskResult.Succeeded();
        }
        if (operation.ParentOperationId.HasValue)
        {
            // Fan-out children are represented inside the parent's notification
            // thread and progress tree; projecting every child would create
            // notification storms for large batches.
            operation.LastProjectedNotificationEventSequence = operation.LatestEventSequence;
            await repository.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return TaskResult.Succeeded();
        }
        var milestones = await repository.GetDbSet<NhBackgroundOperationEvent>()
            .Where(x => x.OperationId == operationId
                        && x.IsMilestone
                        && !x.IsOperatorOnly
                        && x.Sequence > operation.LastProjectedNotificationEventSequence)
            .OrderBy(x => x.Sequence)
            .ToListAsync(cancellationToken);
        if (milestones.Count == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return TaskResult.Succeeded();
        }

        foreach (var milestone in milestones)
        {
            var content = await notificationFormatter.FormatAsync(operation, milestone, cancellationToken);
            if (!operation.UserNotificationId.HasValue)
            {
                var createResult = await notificationService.CreateAsync(
                    new NhUserNotificationMutateModel
                    {
                        UserId = operation.OwnerUserId,
                        Title = content.Title,
                        Message = content.Message,
                        Url = $"{_options.OperationUrlPrefix.TrimEnd('/')}/{operation.Id}",
                        UrlInNewTab = false
                    },
                    cancellationToken: cancellationToken);
                if (!createResult.Success)
                {
                    return TaskResult.Failed(createResult);
                }

                if (createResult.Data is null)
                {
                    return TaskResult.Failed(
                        "notification-create-failed",
                        "background-operation.notification-create-failed");
                }

                operation.UserNotificationId = createResult.Data.Id;
            }
            else
            {
                var addResult = await notificationService.AddMessageAsync(
                    operation.UserNotificationId.Value,
                    new NhAddMessageUserNotificationMutateModel
                    {
                        Title = content.Title,
                        Message = content.Message
                    },
                    cancellationToken);
                if (!addResult.Success)
                {
                    return TaskResult.Failed(addResult);
                }
            }
            operation.LastProjectedNotificationEventSequence = milestone.Sequence;
        }

        await repository.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return TaskResult.Succeeded();
    }
}

internal sealed class NhDefaultBackgroundOperationNotificationFormatter :
    INhBackgroundOperationNotificationFormatter
{
    public Task<NhBackgroundOperationNotificationContent> FormatAsync(
        NhBackgroundOperation operation,
        NhBackgroundOperationEvent milestone,
        CancellationToken cancellationToken = default)
    {
        var message = milestone.MessageKey switch
        {
            "background-operation.queued" => "The operation was queued.",
            "background-operation.started" => "The operation started.",
            "background-operation.succeeded" => "The operation completed successfully.",
            "background-operation.failed" => "The operation failed.",
            "background-operation.cancelled" => "The operation was cancelled.",
            "background-operation.timed-out" => "The operation timed out.",
            "background-operation.cancellation-requested" => "Cancellation was requested.",
            "background-operation.retry-requested" => "A retry was requested.",
            "background-operation.retry-scheduled" => "A retry was scheduled.",
            "background-operation.result-available" => "The operation result is available.",
            "background-operation.unsupported-payload-schema" => "The operation payload version is no longer supported.",
            "background-operation.child-operation-failed" => "One or more child operations failed.",
            _ => milestone.Severity switch
            {
                NhBackgroundOperationMessageSeverity.Success => "A milestone completed successfully.",
                NhBackgroundOperationMessageSeverity.Warning => "The operation published a warning milestone.",
                NhBackgroundOperationMessageSeverity.Error => "The operation published an error milestone.",
                _ => "The operation reached a new milestone."
            }
        };
        return Task.FromResult(new NhBackgroundOperationNotificationContent(
            $"Background operation: {operation.OperationType}",
            message));
    }
}

internal sealed class NhNoOpBackgroundOperationNotificationProjector : INhBackgroundOperationNotificationProjector
{
    public Task<TaskResult> ProjectAsync(
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(TaskResult.Succeeded());
    }
}
