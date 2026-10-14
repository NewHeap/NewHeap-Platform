using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.Common.Actors;

/// <summary>
/// Removes what platform modules keep about a user or a division. Call it from the application's
/// own deletion flow, inside the transaction that deletes the user or division. NewHeap Identity
/// calls <see cref="ForgetUserAsync"/> when it deletes a user.
/// </summary>
public interface INhRetention
{
    Task<TaskResult> ForgetUserAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<TaskResult> ForgetDivisionAsync(Guid divisionId, CancellationToken cancellationToken = default);
}

/// <summary>
/// One module's part of <see cref="INhRetention"/>. A participant must be idempotent: a second
/// call for the same user or division changes nothing.
/// </summary>
public interface INhRetentionParticipant
{
    /// <summary>
    /// Participants run in ascending order. A module whose rows refer to rows of another module
    /// runs first, so the referenced rows are no longer in use when their module removes them.
    /// </summary>
    int Order { get; }

    Task<TaskResult> ForgetUserAsync(Guid userId, CancellationToken cancellationToken);

    Task<TaskResult> ForgetDivisionAsync(Guid divisionId, CancellationToken cancellationToken);
}

/// <summary>
/// Runs every registered <see cref="INhRetentionParticipant"/> in order and stops at the first
/// failure, so the caller can roll its transaction back.
/// </summary>
public sealed class NhRetention : INhRetention
{
    private readonly IReadOnlyList<INhRetentionParticipant> _participants;

    public NhRetention(IEnumerable<INhRetentionParticipant> participants)
    {
        ArgumentNullException.ThrowIfNull(participants);

        _participants = participants
            .OrderBy(participant => participant.Order)
            .ToArray();
    }

    public Task<TaskResult> ForgetUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        return RunAsync((participant, token) => participant.ForgetUserAsync(userId, token), cancellationToken);
    }

    public Task<TaskResult> ForgetDivisionAsync(Guid divisionId, CancellationToken cancellationToken = default)
    {
        return RunAsync((participant, token) => participant.ForgetDivisionAsync(divisionId, token), cancellationToken);
    }

    private async Task<TaskResult> RunAsync(
        Func<INhRetentionParticipant, CancellationToken, Task<TaskResult>> forget,
        CancellationToken cancellationToken)
    {
        foreach (var participant in _participants)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await forget(participant, cancellationToken);
            if (!result.Success)
            {
                return TaskResult.Failed(result);
            }
        }

        return TaskResult.Succeeded();
    }
}
