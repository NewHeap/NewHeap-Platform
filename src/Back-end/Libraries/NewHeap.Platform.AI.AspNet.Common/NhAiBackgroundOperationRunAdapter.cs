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
    /// <summary>
    /// Binds an agent invocation context to the running operation: the operation id becomes
    /// the run id, and the attempt, idempotency key and fencing token come from the operation.
    /// The context must belong to a non-human agent actor and name an accountable party; an
    /// agent that answers for itself uses its own actor id as the accountable party.
    /// </summary>
    NhAiInvocationContext BindInvocation(
        NhAiInvocationContext context,
        INhBackgroundOperationContext operation,
        DateTimeOffset? deadline = null);

    /// <summary>
    /// Binds the execution of an approved proposal. The context keeps the proposal's origin
    /// run id, so the approval stays valid, and records the executing operation as the
    /// correlation id. <paramref name="fenced"/> passes the operation's fencing token to the
    /// idempotency lease; leave it off for idempotent tools, whose retries must replay rather
    /// than conflict.
    /// </summary>
    NhAiInvocationContext BindProposalExecution(
        NhAiInvocationContext context,
        INhBackgroundOperationContext operation,
        string proposalRunId,
        bool fenced,
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

    public NhAiInvocationContext BindProposalExecution(
        NhAiInvocationContext context,
        INhBackgroundOperationContext operation,
        string proposalRunId,
        bool fenced,
        DateTimeOffset? deadline = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(proposalRunId);
        if (context.ActorKind != NhAiActorKind.Agent
            || string.IsNullOrWhiteSpace(context.AccountableOwnerId))
        {
            throw new InvalidOperationException(
                "A proposal execution requires a non-human agent actor and an accountable owner.");
        }
        if (deadline.HasValue && deadline.Value <= DateTimeOffset.UtcNow)
        {
            throw new ArgumentOutOfRangeException(nameof(deadline));
        }

        return context with
        {
            RunId = proposalRunId,
            RunAttemptNumber = operation.AttemptNumber,
            CorrelationId = operation.OperationId.ToString("N"),
            IdempotencyKey = operation.IdempotencyKey,
            FencingToken = fenced
                ? operation.FencingToken.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : null,
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
