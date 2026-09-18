using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;

namespace NewHeap.Platform.AI.Chat.Governance;

/// <summary>
/// Durable idempotency leases on <c>AssistantIdempotencyLease</c>. A key is unique per actor, tool
/// and tool version. A completed key with the same arguments is a duplicate; different arguments
/// are a conflict. An in-progress lease blocks other callers until it expires; an expired lease can
/// be taken over with a new lease id and generation, after which the previous holder can no longer
/// complete it.
/// </summary>
internal sealed class NhAssistantIdempotencyManager(
    NhAssistantDbContextFactory contextFactory,
    NhAssistantRegistrationState state) : INhAiIdempotencyManager
{
    public async ValueTask<NhAiIdempotencyLease> AcquireAsync(
        NhAiIdempotencyRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ActorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ToolId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.IdempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ArgumentHash);
        if (request.ActorId.Length > 256
            || request.ToolId.Length > 256
            || request.IdempotencyKey.Length > 256
            || request.ArgumentHash.Length > 128
            || request.FencingToken is { Length: > 256 })
        {
            return new NhAiIdempotencyLease(NhAiIdempotencyDecisionKind.Denied, "idempotency-request-invalid");
        }

        var keyHash = ComputeKeyHash(request);
        var now = DateTimeOffset.UtcNow;
        var leaseId = Guid.NewGuid().ToString("N");
        var expiresAt = now.Add(state.Limits.IdempotencyLeaseDuration);
        await using (var context = contextFactory.CreateDbContext())
        {
            context.IdempotencyLeases.Add(new AssistantIdempotencyLease
            {
                Id = Guid.NewGuid(),
                KeyHash = keyHash,
                ActorId = request.ActorId,
                ToolId = request.ToolId,
                ToolVersion = request.ToolVersion,
                IdempotencyKey = request.IdempotencyKey,
                ArgumentHash = request.ArgumentHash,
                FencingToken = request.FencingToken,
                LeaseId = leaseId,
                Generation = 1,
                Status = NhAssistantLeaseStatuses.InProgress,
                AcquiredAt = now,
                ExpiresAt = expiresAt
            });
            try
            {
                await context.SaveChangesAsync(cancellationToken);
                return new NhAiIdempotencyLease(NhAiIdempotencyDecisionKind.Acquired, "acquired", leaseId);
            }
            catch (DbUpdateException)
            {
                // The key exists; decide below against the stored lease.
            }
        }

        await using var lookup = contextFactory.CreateDbContext();
        var existing = await lookup.IdempotencyLeases
            .AsNoTracking()
            .SingleOrDefaultAsync(lease => lease.KeyHash == keyHash, cancellationToken)
            ?? throw new InvalidOperationException("The assistant idempotency lease disappeared while it was being acquired.");
        if (!string.Equals(existing.ArgumentHash, request.ArgumentHash, StringComparison.Ordinal)
            || !string.Equals(existing.FencingToken, request.FencingToken, StringComparison.Ordinal))
        {
            return new NhAiIdempotencyLease(NhAiIdempotencyDecisionKind.Conflict, "idempotency-conflict");
        }
        if (existing.Status == NhAssistantLeaseStatuses.Completed)
        {
            return new NhAiIdempotencyLease(NhAiIdempotencyDecisionKind.Duplicate, "duplicate");
        }
        if (existing.ExpiresAt > now)
        {
            return new NhAiIdempotencyLease(NhAiIdempotencyDecisionKind.Conflict, "idempotency-in-progress");
        }

        var takenOver = await lookup.IdempotencyLeases
            .Where(lease => lease.Id == existing.Id
                && lease.LeaseId == existing.LeaseId
                && lease.Status == NhAssistantLeaseStatuses.InProgress)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(lease => lease.LeaseId, leaseId)
                    .SetProperty(lease => lease.Generation, lease => lease.Generation + 1)
                    .SetProperty(lease => lease.AcquiredAt, now)
                    .SetProperty(lease => lease.ExpiresAt, expiresAt),
                cancellationToken);
        return takenOver == 1
            ? new NhAiIdempotencyLease(NhAiIdempotencyDecisionKind.Acquired, "acquired-after-expiry", leaseId)
            : new NhAiIdempotencyLease(NhAiIdempotencyDecisionKind.Conflict, "idempotency-in-progress");
    }

    public async ValueTask CompleteAsync(
        NhAiIdempotencyLease lease,
        NhAiOutcomeKind outcome,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (lease.Decision != NhAiIdempotencyDecisionKind.Acquired || string.IsNullOrWhiteSpace(lease.LeaseId))
        {
            throw new InvalidOperationException("Only an acquired assistant idempotency lease can be completed.");
        }

        var now = DateTimeOffset.UtcNow;
        var outcomeCode = outcome.ToString();
        await using var context = contextFactory.CreateDbContext();
        var completed = await context.IdempotencyLeases
            .Where(item => item.LeaseId == lease.LeaseId && item.Status == NhAssistantLeaseStatuses.InProgress)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(item => item.Status, NhAssistantLeaseStatuses.Completed)
                    .SetProperty(item => item.Outcome, outcomeCode)
                    .SetProperty(item => item.CompletedAt, now),
                cancellationToken);
        if (completed != 1)
        {
            // The fencing guarantee is lost: another caller took over the expired lease.
            throw new InvalidOperationException("The assistant idempotency lease was lost or already completed.");
        }
    }

    private static string ComputeKeyHash(NhAiIdempotencyRequest request)
    {
        var material = string.Join(
            "\n",
            request.ActorId,
            request.ToolId,
            request.ToolVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            request.IdempotencyKey);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }
}
