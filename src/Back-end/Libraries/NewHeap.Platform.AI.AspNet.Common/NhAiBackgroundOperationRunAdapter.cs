using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.AspNet;

public sealed record NhAiBackgroundApprovalSignal(
    Guid ApprovalId,
    Guid ProposalId,
    string ProposalHash,
    bool Approved,
    string DecisionCode);

public interface INhAiBackgroundOperationRunAdapter
{
    NhAiInvocationContext BindInvocation(
        NhAiInvocationContext context,
        INhBackgroundOperationContext operation,
        DateTimeOffset? deadline = null);

    Task<NhAiRunCheckpointReference?> GetCheckpointAsync(
        INhBackgroundOperationContext operation,
        CancellationToken cancellationToken = default);

    Task<TaskResult> SetCheckpointAsync(
        INhBackgroundOperationContext operation,
        NhAiRunCheckpointReference checkpoint,
        long? expectedVersion = null,
        CancellationToken cancellationToken = default);

    Task<NhBackgroundOperationSignalWaitResult<NhAiBackgroundApprovalSignal>> WaitForApprovalAsync(
        INhBackgroundOperationContext operation,
        string waitKey,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default);
}

internal sealed class NhAiBackgroundOperationRunAdapter : INhAiBackgroundOperationRunAdapter
{
    private const string CheckpointKey = "ai-run-checkpoint";

    public NhAiInvocationContext BindInvocation(
        NhAiInvocationContext context,
        INhBackgroundOperationContext operation,
        DateTimeOffset? deadline = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(operation);
        if (context.ActorKind != NhAiActorKind.Agent
            || string.IsNullOrWhiteSpace(context.AccountableOwnerId))
        {
            throw new InvalidOperationException(
                "A durable AI run requires a non-human agent actor and an accountable owner.");
        }

        var runId = operation.OperationId.ToString("N");
        if (!string.IsNullOrWhiteSpace(context.RunId)
            && !string.Equals(context.RunId, runId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The AI run ID does not match the durable background operation.");
        }
        if (deadline.HasValue && deadline.Value <= DateTimeOffset.UtcNow)
        {
            throw new ArgumentOutOfRangeException(nameof(deadline));
        }

        return context with
        {
            RunId = runId,
            RunAttemptNumber = operation.AttemptNumber,
            CorrelationId = context.CorrelationId ?? runId,
            IdempotencyKey = operation.IdempotencyKey,
            FencingToken = operation.FencingToken.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            Deadline = MinDeadline(context.Deadline, deadline)
        };
    }

    public async Task<NhAiRunCheckpointReference?> GetCheckpointAsync(
        INhBackgroundOperationContext operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var checkpoint = await operation.Checkpoints.GetAsync<NhAiRunCheckpointReference>(
            CheckpointKey,
            cancellationToken);
        return checkpoint?.Value;
    }

    public Task<TaskResult> SetCheckpointAsync(
        INhBackgroundOperationContext operation,
        NhAiRunCheckpointReference checkpoint,
        long? expectedVersion = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(checkpoint);
        _ = NhAiRunCheckpointReferenceFactory.Create(
            checkpoint.AdapterId,
            checkpoint.WorkflowId,
            checkpoint.WorkflowVersion,
            checkpoint.CheckpointId,
            checkpoint.CheckpointSchemaVersion,
            checkpoint.StateHash,
            checkpoint.CreatedAt,
            checkpoint.SessionId);
        return operation.Checkpoints.SetAsync(
            CheckpointKey,
            checkpoint,
            checkpoint.CheckpointSchemaVersion,
            expectedVersion,
            cancellationToken);
    }

    public Task<NhBackgroundOperationSignalWaitResult<NhAiBackgroundApprovalSignal>> WaitForApprovalAsync(
        INhBackgroundOperationContext operation,
        string waitKey,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return operation.Suspension.WaitForSignalAsync<NhAiBackgroundApprovalSignal>(
            waitKey,
            expiresAt,
            cancellationToken: cancellationToken);
    }

    private static DateTimeOffset? MinDeadline(
        DateTimeOffset? contextDeadline,
        DateTimeOffset? operationDeadline)
    {
        if (!contextDeadline.HasValue)
        {
            return operationDeadline;
        }
        if (!operationDeadline.HasValue)
        {
            return contextDeadline;
        }
        return contextDeadline.Value <= operationDeadline.Value
            ? contextDeadline
            : operationDeadline;
    }
}
