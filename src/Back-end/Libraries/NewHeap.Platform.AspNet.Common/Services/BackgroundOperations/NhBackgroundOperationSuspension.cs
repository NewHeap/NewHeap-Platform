using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;

internal sealed class NhBackgroundOperationSignalPendingException : Exception;

internal enum NhBackgroundOperationSignalCheckpointStatus
{
    Waiting = 0,
    Signaled = 10
}

internal sealed record NhBackgroundOperationSignalCheckpoint<TSignal>(
    string WaitKey,
    int SignalSchemaVersion,
    DateTimeOffset ExpiresAt,
    NhBackgroundOperationSignalCheckpointStatus Status,
    TSignal? Signal,
    string? SignalHash,
    Guid? SignaledByUserId,
    DateTimeOffset? SignaledAt);

internal sealed class NhBackgroundOperationSuspensionContext(
    NhBackgroundOperationAttemptClaim claim,
    NhBackgroundOperationPersistence persistence) : INhBackgroundOperationSuspensionContext
{
    public async Task<NhBackgroundOperationSignalWaitResult<TSignal>> WaitForSignalAsync<TSignal>(
        string waitKey,
        DateTimeOffset expiresAt,
        int signalSchemaVersion = 1,
        CancellationToken cancellationToken = default)
    {
        NhBackgroundOperationKeys.ValidateStepKey(waitKey);
        if (signalSchemaVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(signalSchemaVersion));
        }

        var checkpointKey = NhBackgroundOperationSignalKeys.CheckpointKey(waitKey);
        while (true)
        {
            var persisted = await persistence.GetCheckpointAsync<
                NhBackgroundOperationSignalCheckpoint<TSignal>>(
                claim,
                checkpointKey,
                cancellationToken);
            if (persisted is not null)
            {
                ValidateContract(
                    persisted.Value,
                    waitKey,
                    expiresAt,
                    signalSchemaVersion);
                if (persisted.Value.Status == NhBackgroundOperationSignalCheckpointStatus.Signaled)
                {
                    return new NhBackgroundOperationSignalWaitResult<TSignal>(
                        NhBackgroundOperationSignalWaitStatus.Signaled,
                        persisted.Value.Signal,
                        persisted.Value.SignaledByUserId,
                        persisted.Value.SignaledAt,
                        persisted.Value.ExpiresAt);
                }
                if (DateTimeOffset.UtcNow >= persisted.Value.ExpiresAt)
                {
                    return new NhBackgroundOperationSignalWaitResult<TSignal>(
                        NhBackgroundOperationSignalWaitStatus.Expired,
                        default,
                        null,
                        null,
                        persisted.Value.ExpiresAt);
                }
            }
            else if (DateTimeOffset.UtcNow >= expiresAt)
            {
                return new NhBackgroundOperationSignalWaitResult<TSignal>(
                    NhBackgroundOperationSignalWaitStatus.Expired,
                    default,
                    null,
                    null,
                    expiresAt);
            }

            var suspended = await persistence.SuspendForSignalAsync(
                claim,
                checkpointKey,
                new NhBackgroundOperationSignalCheckpoint<TSignal>(
                    waitKey,
                    signalSchemaVersion,
                    expiresAt,
                    NhBackgroundOperationSignalCheckpointStatus.Waiting,
                    default,
                    null,
                    null,
                    null),
                cancellationToken);
            if (suspended)
            {
                throw new NhBackgroundOperationSignalPendingException();
            }

            // A signal won the lock between the read and suspension attempt.
            // Re-read it instead of consuming retry budget or losing the wake-up.
        }
    }

    private static void ValidateContract<TSignal>(
        NhBackgroundOperationSignalCheckpoint<TSignal> checkpoint,
        string waitKey,
        DateTimeOffset expiresAt,
        int signalSchemaVersion)
    {
        if (!string.Equals(checkpoint.WaitKey, waitKey, StringComparison.Ordinal)
            || checkpoint.SignalSchemaVersion != signalSchemaVersion
            || checkpoint.ExpiresAt != expiresAt)
        {
            throw new InvalidOperationException(
                $"Background-operation signal contract '{waitKey}' changed after suspension.");
        }
    }
}

internal sealed class NhBackgroundOperationSignalService(
    NhBackgroundOperationPersistence persistence) : INhBackgroundOperationSignalService
{
    public Task<TaskResult<NhBackgroundOperationSignalWriteResult>> SignalForOwnerAsync<TSignal>(
        Guid operationId,
        Guid ownerUserId,
        Guid signaledByUserId,
        string waitKey,
        TSignal signal,
        int signalSchemaVersion = 1,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("An operation ID is required.", nameof(operationId));
        }
        if (ownerUserId == Guid.Empty)
        {
            throw new ArgumentException("An operation owner ID is required.", nameof(ownerUserId));
        }
        if (signaledByUserId == Guid.Empty)
        {
            throw new ArgumentException("A signaling actor ID is required.", nameof(signaledByUserId));
        }
        ArgumentNullException.ThrowIfNull(signal);
        NhBackgroundOperationKeys.ValidateStepKey(waitKey);
        if (signalSchemaVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(signalSchemaVersion));
        }

        return persistence.SignalForOwnerAsync(
            operationId,
            ownerUserId,
            signaledByUserId,
            NhBackgroundOperationSignalKeys.CheckpointKey(waitKey),
            waitKey,
            signal,
            signalSchemaVersion,
            cancellationToken);
    }
}

internal static class NhBackgroundOperationSignalKeys
{
    internal static string CheckpointKey(string waitKey)
    {
        var hash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(waitKey)));
        return $"signal-{hash[..40]}";
    }

    internal static string HashSignal<TSignal>(TSignal signal)
    {
        var json = JsonSerializer.Serialize(signal, NhBackgroundOperationJson.Options);
        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }
}

internal sealed partial class NhBackgroundOperationPersistence
{
    internal async Task<bool> SuspendForSignalAsync<TSignal>(
        NhBackgroundOperationAttemptClaim claim,
        string checkpointKey,
        NhBackgroundOperationSignalCheckpoint<TSignal> requested,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(requested, NhBackgroundOperationJson.Options);
        if (Encoding.UTF8.GetByteCount(json) > _options.MaxCheckpointBytes)
        {
            throw new InvalidOperationException(
                "The background-operation signal contract exceeds the checkpoint limit.");
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRepository<NhBackgroundOperation>>();
        await using var transaction = await repository.StartOrGetTransactionScopeAsync(cancellationToken);
        if (!await LockAsync(repository, transaction, claim.OperationId, cancellationToken))
        {
            throw new NhBackgroundOperationContentionSignal(TimeSpan.FromSeconds(2));
        }

        var operation = await LoadFencedOperationAsync(repository, claim, cancellationToken);
        var set = repository.GetDbSet<NhBackgroundOperationCheckpoint>();
        var checkpoint = await set.SingleOrDefaultAsync(
            item => item.OperationId == claim.OperationId
                    && item.CheckpointKey == checkpointKey,
            cancellationToken);
        var created = checkpoint is null;
        if (checkpoint is not null)
        {
            var existing = JsonSerializer.Deserialize<
                NhBackgroundOperationSignalCheckpoint<TSignal>>(
                checkpoint.ValueJson,
                NhBackgroundOperationJson.Options)
                ?? throw new InvalidOperationException(
                    $"Signal checkpoint '{checkpointKey}' contains no value.");
            if (!string.Equals(existing.WaitKey, requested.WaitKey, StringComparison.Ordinal)
                || existing.SignalSchemaVersion != requested.SignalSchemaVersion
                || existing.ExpiresAt != requested.ExpiresAt)
            {
                throw new InvalidOperationException(
                    $"Background-operation signal contract '{requested.WaitKey}' changed after suspension.");
            }
            if (existing.Status == NhBackgroundOperationSignalCheckpointStatus.Signaled)
            {
                await transaction.CommitAsync(cancellationToken);
                return false;
            }
        }
        else
        {
            var now = DateTimeOffset.UtcNow;
            checkpoint = new NhBackgroundOperationCheckpoint
            {
                OperationId = claim.OperationId,
                CheckpointKey = checkpointKey,
                SchemaVersion = 1,
                ValueJson = json,
                AttemptId = claim.AttemptId,
                CreationDateTime = now,
                LastModifiedDateTime = now,
                Version = 1
            };
            set.Add(checkpoint);
        }

        var suspensionTime = DateTimeOffset.UtcNow;
        var attempt = await repository.GetDbSet<NhBackgroundOperationAttempt>()
            .SingleAsync(item => item.Id == claim.AttemptId, cancellationToken);
        attempt.Status = NhBackgroundOperationAttemptStatus.Suspended;
        attempt.CompletedAt = suspensionTime;
        attempt.LastModifiedDateTime = suspensionTime;
        attempt.Version++;
        operation.CurrentAttemptId = null;
        operation.Status = NhBackgroundOperationStatus.WaitingForSignal;
        operation.SchedulerJobId = null;
        operation.NextDispatchAt = requested.ExpiresAt;
        operation.HeartbeatAt = suspensionTime;
        NhBackgroundOperationService.Touch(operation, suspensionTime);
        if (created)
        {
            NhBackgroundOperationService.AppendEvent(
                operation,
                NhBackgroundOperationEventType.SignalWaitStarted,
                NhBackgroundOperationMessageSeverity.Information,
                "background-operation.signal-wait-started",
                new { waitKey = requested.WaitKey, requested.ExpiresAt },
                true,
                claim.AttemptId);
            await NhBackgroundOperationEventRetention.TrimAsync(
                repository,
                operation,
                _options,
                cancellationToken);
        }
        await repository.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await PublishSafelyAsync(operation, cancellationToken);
        return true;
    }

    internal async Task<TaskResult<NhBackgroundOperationSignalWriteResult>> SignalForOwnerAsync<TSignal>(
        Guid operationId,
        Guid ownerUserId,
        Guid signaledByUserId,
        string checkpointKey,
        string waitKey,
        TSignal signal,
        int signalSchemaVersion,
        CancellationToken cancellationToken)
    {
        var signalHash = NhBackgroundOperationSignalKeys.HashSignal(signal);
        await using var scope = _scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRepository<NhBackgroundOperation>>();
        await using var transaction = await repository.StartOrGetTransactionScopeAsync(cancellationToken);
        if (!await LockAsync(repository, transaction, operationId, cancellationToken))
        {
            return TaskResult<NhBackgroundOperationSignalWriteResult>.Failed(
                "The operation is busy. Please retry signaling.");
        }

        var operation = await repository.GetAll().SingleOrDefaultAsync(
            item => item.Id == operationId && item.OwnerUserId == ownerUserId,
            cancellationToken);
        var checkpoint = await repository.GetDbSet<NhBackgroundOperationCheckpoint>()
            .SingleOrDefaultAsync(
                item => item.OperationId == operationId
                        && item.CheckpointKey == checkpointKey,
                cancellationToken);
        if (operation is null || checkpoint is null)
        {
            return TaskResult<NhBackgroundOperationSignalWriteResult>.Failed(
                "The suspended operation signal was not found.");
        }

        var persisted = JsonSerializer.Deserialize<
            NhBackgroundOperationSignalCheckpoint<TSignal>>(
            checkpoint.ValueJson,
            NhBackgroundOperationJson.Options)
            ?? throw new InvalidOperationException(
                $"Signal checkpoint '{checkpointKey}' contains no value.");
        if (!string.Equals(persisted.WaitKey, waitKey, StringComparison.Ordinal)
            || persisted.SignalSchemaVersion != signalSchemaVersion)
        {
            return TaskResult<NhBackgroundOperationSignalWriteResult>.Failed(
                "The signal does not match the suspended operation contract.");
        }
        if (persisted.Status == NhBackgroundOperationSignalCheckpointStatus.Signaled)
        {
            if (!string.Equals(persisted.SignalHash, signalHash, StringComparison.Ordinal))
            {
                return TaskResult<NhBackgroundOperationSignalWriteResult>.Failed(
                    "A different signal was already accepted for this wait.");
            }
            await transaction.CommitAsync(cancellationToken);
            return TaskResult<NhBackgroundOperationSignalWriteResult>.Succeeded(
                new NhBackgroundOperationSignalWriteResult(
                    NhBackgroundOperationSignalWriteStatus.Duplicate,
                    operationId,
                    waitKey,
                    persisted.SignaledAt!.Value));
        }

        var now = DateTimeOffset.UtcNow;
        if (persisted.ExpiresAt <= now)
        {
            return TaskResult<NhBackgroundOperationSignalWriteResult>.Failed(
                "The suspended operation signal has expired.");
        }
        if (operation.Status != NhBackgroundOperationStatus.WaitingForSignal
            || operation.CurrentAttemptId.HasValue)
        {
            return TaskResult<NhBackgroundOperationSignalWriteResult>.Failed(
                "The operation is not waiting for this signal.");
        }

        var accepted = persisted with
        {
            Status = NhBackgroundOperationSignalCheckpointStatus.Signaled,
            Signal = signal,
            SignalHash = signalHash,
            SignaledByUserId = signaledByUserId,
            SignaledAt = now
        };
        var acceptedJson = JsonSerializer.Serialize(accepted, NhBackgroundOperationJson.Options);
        if (Encoding.UTF8.GetByteCount(acceptedJson) > _options.MaxCheckpointBytes)
        {
            return TaskResult<NhBackgroundOperationSignalWriteResult>.Failed(
                "The signal exceeds the configured checkpoint limit.");
        }

        checkpoint.ValueJson = acceptedJson;
        checkpoint.LastModifiedDateTime = now;
        checkpoint.Version++;
        operation.Status = NhBackgroundOperationStatus.PendingDispatch;
        operation.NextDispatchAt = now;
        operation.SchedulerJobId = null;
        NhBackgroundOperationService.Touch(operation, now);
        NhBackgroundOperationService.AppendEvent(
            operation,
            NhBackgroundOperationEventType.SignalReceived,
            NhBackgroundOperationMessageSeverity.Information,
            "background-operation.signal-received",
            new { waitKey },
            true);
        await NhBackgroundOperationEventRetention.TrimAsync(
            repository,
            operation,
            _options,
            cancellationToken);
        await repository.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await PublishSafelyAsync(operation, cancellationToken);
        return TaskResult<NhBackgroundOperationSignalWriteResult>.Succeeded(
            new NhBackgroundOperationSignalWriteResult(
                NhBackgroundOperationSignalWriteStatus.Accepted,
                operationId,
                waitKey,
                now));
    }
}
