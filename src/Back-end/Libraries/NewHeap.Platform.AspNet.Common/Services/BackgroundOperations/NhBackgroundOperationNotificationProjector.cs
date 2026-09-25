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
        var notificationPolicy = scope.ServiceProvider.GetRequiredService<INhBackgroundOperationNotificationPolicy>();
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
            var decision = await notificationPolicy.DecideAsync(operation, milestone, cancellationToken);
            if (decision.ShouldNotify)
            {
                var projectionResult = await ProjectMilestoneAsync(
                    notificationService,
                    operation,
                    milestone,
                    decision,
                    cancellationToken);
                if (!projectionResult.Success)
                {
                    return projectionResult;
                }
            }

            operation.LastProjectedNotificationEventSequence = milestone.Sequence;
        }

        await repository.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return TaskResult.Succeeded();
    }

    private async Task<TaskResult> ProjectMilestoneAsync(
        INhUserNotificationService notificationService,
        NhBackgroundOperation operation,
        NhBackgroundOperationEvent milestone,
        NhBackgroundOperationNotificationDecision decision,
        CancellationToken cancellationToken)
    {
        var content = decision.Content!;
        var severity = decision.Severity ?? ToNotificationSeverity(milestone.Severity);
        var url = string.IsNullOrWhiteSpace(decision.Url)
            ? $"{_options.OperationUrlPrefix.TrimEnd('/')}/{operation.Id}"
            : decision.Url;

        if (operation.UserNotificationId.HasValue)
        {
            var current = await notificationService.GetAsync(operation.UserNotificationId.Value, cancellationToken);
            if (current is { IsArchived: false })
            {
                return await notificationService.AddMessageAsync(
                    current.Id,
                    new NhAddMessageUserNotificationMutateModel
                    {
                        Title = content.Title,
                        Message = content.Message,
                        Severity = severity,
                        Url = url
                    },
                    cancellationToken);
            }
        }

        // A new or archived thread starts again; a group key joins an active thread
        // of a related operation instead.
        var createResult = await notificationService.CreateOrAddMessageAsync(
            new NhUserNotificationMutateModel
            {
                UserId = operation.OwnerUserId,
                Title = content.Title,
                Message = content.Message,
                Url = url,
                UrlInNewTab = false,
                Category = decision.Category ?? NhBackgroundOperationNotificationCategories.BackgroundOperation,
                Severity = severity,
                GroupKey = decision.GroupKey
            },
            cancellationToken);
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
        return TaskResult.Succeeded();
    }

    private static NhUserNotificationSeverity ToNotificationSeverity(NhBackgroundOperationMessageSeverity severity)
    {
        return severity switch
        {
            NhBackgroundOperationMessageSeverity.Success => NhUserNotificationSeverity.Success,
            NhBackgroundOperationMessageSeverity.Warning => NhUserNotificationSeverity.Warning,
            NhBackgroundOperationMessageSeverity.Error => NhUserNotificationSeverity.Error,
            _ => NhUserNotificationSeverity.Information
        };
    }
}

/// <summary>
/// Default notification policy. It notifies the owner only about outcomes and
/// requests for attention: success, failure, time-out, a cancellation the owner did
/// not request (for example one requested by an administrator), a wait for input,
/// required operator recovery, and milestones that a handler published itself. Lifecycle progress such as start, retry scheduling and
/// intermediate results stays in the progress view. Compose or replace it through
/// <see cref="NhBackgroundOperationBuilder.UseNotificationPolicy{TPolicy}"/>.
/// </summary>
public class NhDefaultBackgroundOperationNotificationPolicy : INhBackgroundOperationNotificationPolicy
{
    private const string LifecycleMessageKeyPrefix = "background-operation.";

    private static readonly HashSet<string> NotifiedLifecycleMessageKeys = new(StringComparer.Ordinal)
    {
        "background-operation.succeeded",
        "background-operation.failed",
        "background-operation.timedout",
        "background-operation.timed-out",
        "background-operation.cancelled",
        "background-operation.operator-recovery-required",
        "background-operation.signal-wait-started"
    };

    private readonly INhBackgroundOperationNotificationFormatter _formatter;

    public NhDefaultBackgroundOperationNotificationPolicy(INhBackgroundOperationNotificationFormatter formatter)
    {
        _formatter = formatter;
    }

    public virtual async Task<NhBackgroundOperationNotificationDecision> DecideAsync(
        NhBackgroundOperation operation,
        NhBackgroundOperationEvent milestone,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(milestone);

        if (!ShouldNotify(operation, milestone))
        {
            return NhBackgroundOperationNotificationDecision.Skip();
        }

        var content = await _formatter.FormatAsync(operation, milestone, cancellationToken);
        return NhBackgroundOperationNotificationDecision.Notify(content);
    }

    /// <summary>
    /// Returns whether the milestone is worth a notification under the default rules.
    /// </summary>
    protected virtual bool ShouldNotify(NhBackgroundOperation operation, NhBackgroundOperationEvent milestone)
    {
        var messageKey = milestone.MessageKey;
        if (string.IsNullOrWhiteSpace(messageKey)
            || !messageKey.StartsWith(LifecycleMessageKeyPrefix, StringComparison.Ordinal))
        {
            // Handler-published milestones are an explicit request to inform the owner.
            return true;
        }

        if (!NotifiedLifecycleMessageKeys.Contains(messageKey))
        {
            return false;
        }

        // The owner requested the cancellation and already knows about it.
        return messageKey != "background-operation.cancelled" || !IsCancellationRequestedByOwner(operation);
    }

    private static bool IsCancellationRequestedByOwner(NhBackgroundOperation operation)
    {
        if (!operation.CancelRequestedAt.HasValue)
        {
            return false;
        }

        return !operation.CancelRequestedByUserId.HasValue
               || operation.CancelRequestedByUserId == operation.OwnerUserId;
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
            "background-operation.timedout" => "The operation timed out.",
            "background-operation.timed-out" => "The operation timed out.",
            "background-operation.cancellation-requested" => "Cancellation was requested.",
            "background-operation.administrator-cancellation-requested" => "An administrator requested cancellation.",
            "background-operation.retry-requested" => "A retry was requested.",
            "background-operation.administrator-retry-requested" => "An administrator requested a retry.",
            "background-operation.retry-scheduled" => "A retry was scheduled.",
            "background-operation.result-available" => "The operation result is available.",
            "background-operation.signal-wait-started" => "The operation is waiting for your input.",
            "background-operation.operator-recovery-required" => "The operation stopped unexpectedly and needs attention.",
            "background-operation.dispatch-stalled" => "No worker started the operation.",
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
