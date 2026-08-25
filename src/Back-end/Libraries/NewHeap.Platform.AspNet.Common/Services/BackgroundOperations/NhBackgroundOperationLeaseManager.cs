using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;

namespace NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;

/// <summary>
/// Internal execution signal used by the required-lease helpers to unwind the
/// handler and reschedule without consuming a retry. It is intentionally not a
/// public failure contract; callers use the nullable helpers when they want to
/// handle contention themselves.
/// </summary>
internal sealed class NhBackgroundOperationContentionSignal : Exception
{
    internal NhBackgroundOperationContentionSignal(TimeSpan retryAfter)
        : base("A required background-operation resource is currently busy.")
    {
        RetryAfter = retryAfter;
    }

    internal TimeSpan RetryAfter { get; }
}

internal sealed class NhBackgroundOperationLeaseManager : INhBackgroundOperationLeaseManager
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly NhBackgroundOperationsOptions _operationOptions;
    private readonly NhBackgroundOperationAttemptClaim _claim;

    internal NhBackgroundOperationLeaseManager(
        IServiceScopeFactory scopeFactory,
        NhBackgroundOperationsOptions operationOptions,
        NhBackgroundOperationAttemptClaim claim)
    {
        _scopeFactory = scopeFactory;
        _operationOptions = operationOptions;
        _claim = claim;
    }

    public async Task<INhBackgroundOperationLeaseHandle?> AcquireAsync(
        string resourceKey,
        NhBackgroundOperationLeaseOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedKey = NormalizeResourceKey(resourceKey);
        return await AcquireNormalizedAsync(normalizedKey, options, cancellationToken);
    }

    private async Task<INhBackgroundOperationLeaseHandle?> AcquireNormalizedAsync(
        string normalizedKey,
        NhBackgroundOperationLeaseOptions? options,
        CancellationToken cancellationToken)
    {
        options ??= new NhBackgroundOperationLeaseOptions();
        if (options.Slots < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options.Slots));
        }

        var leaseDuration = options.LeaseDuration ?? _operationOptions.DefaultLeaseDuration;
        if (leaseDuration <= _operationOptions.HeartbeatInterval)
        {
            throw new ArgumentException("Lease duration must exceed the operation heartbeat interval.", nameof(options));
        }

        if (options.WaitTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options.WaitTimeout));
        }

        var deadline = DateTimeOffset.UtcNow + options.WaitTimeout;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var acquired = await TryAcquireAsync(normalizedKey, options.Slots, leaseDuration, cancellationToken);
            if (acquired is not null)
            {
                return acquired;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                return null;
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            await Task.Delay(remaining < TimeSpan.FromMilliseconds(250) ? remaining : TimeSpan.FromMilliseconds(250), cancellationToken);
        } while (true);
    }

    public async Task<INhBackgroundOperationLeaseSet?> AcquireManyAsync(
        IEnumerable<string> resourceKeys,
        NhBackgroundOperationLeaseOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resourceKeys);
        var keys = resourceKeys.Select(NormalizeResourceKey)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        if (keys.Length == 0)
        {
            throw new ArgumentException("At least one resource key is required.", nameof(resourceKeys));
        }
        var leases = new List<INhBackgroundOperationLeaseHandle>();
        try
        {
            foreach (var key in keys)
            {
                var lease = await AcquireNormalizedAsync(key, options, cancellationToken);
                if (lease is null)
                {
                    await ReleaseReverseAsync(leases);
                    return null;
                }

                leases.Add(lease);
            }

            return new NhBackgroundOperationLeaseSet(leases);
        }
        catch
        {
            await ReleaseReverseAsync(leases);
            throw;
        }
    }

    public async Task<INhBackgroundOperationLeaseHandle> AcquireRequiredAsync(
        string resourceKey,
        NhBackgroundOperationLeaseOptions? options = null,
        TimeSpan? rescheduleDelay = null,
        CancellationToken cancellationToken = default)
    {
        var lease = await AcquireAsync(resourceKey, options, cancellationToken);
        return lease ?? throw new NhBackgroundOperationContentionSignal(
            ValidateRescheduleDelay(rescheduleDelay));
    }

    public async Task<INhBackgroundOperationLeaseSet> AcquireManyRequiredAsync(
        IEnumerable<string> resourceKeys,
        NhBackgroundOperationLeaseOptions? options = null,
        TimeSpan? rescheduleDelay = null,
        CancellationToken cancellationToken = default)
    {
        var leases = await AcquireManyAsync(resourceKeys, options, cancellationToken);
        return leases ?? throw new NhBackgroundOperationContentionSignal(
            ValidateRescheduleDelay(rescheduleDelay));
    }

    private async Task<INhBackgroundOperationLeaseHandle?> TryAcquireAsync(
        string resourceKey,
        int slots,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRepository<NhBackgroundOperationLease>>();
        await using var transaction = await repository.StartOrGetTransactionScopeAsync(cancellationToken);
        if (!await repository.TryAcquireTransactionLockAsync(
                transaction,
                $"NhBackgroundOperation:Lease:{resourceKey}",
                _operationOptions.TransactionLockTimeoutMilliseconds,
                cancellationToken))
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var set = repository.GetAll();
        for (var slot = 0; slot < slots; slot++)
        {
            var lease = await set.SingleOrDefaultAsync(x => x.ResourceKey == resourceKey && x.Slot == slot, cancellationToken);
            if (lease is not null
                && lease.OperationId == _claim.OperationId
                && lease.AttemptId == _claim.AttemptId)
            {
                lease.HeartbeatAt = now;
                lease.ExpiresAt = now + leaseDuration;
                lease.Version++;
                await repository.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return CreateHandle(lease, leaseDuration);
            }
            if (lease is not null && lease.OperationId.HasValue && lease.ExpiresAt > now)
            {
                continue;
            }

            if (lease is null)
            {
                lease = new NhBackgroundOperationLease
                {
                    ResourceKey = resourceKey,
                    Slot = slot,
                    FencingToken = 0
                };
                await repository.AddAsync(lease, cancellationToken);
            }
            lease.OperationId = _claim.OperationId;
            lease.AttemptId = _claim.AttemptId;
            lease.AcquiredAt = now;
            lease.HeartbeatAt = now;
            lease.ExpiresAt = now + leaseDuration;
            lease.FencingToken++;
            lease.Version++;
            await repository.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return CreateHandle(lease, leaseDuration);
        }
        await transaction.CommitAsync(cancellationToken);
        return null;
    }

    private NhBackgroundOperationLeaseHandle CreateHandle(
        NhBackgroundOperationLease lease,
        TimeSpan leaseDuration)
    {
        return new NhBackgroundOperationLeaseHandle(
            _scopeFactory,
            _operationOptions,
            _claim,
            lease.ResourceKey,
            lease.Slot,
            lease.FencingToken,
            lease.ExpiresAt!.Value,
            leaseDuration);
    }

    private static async Task ReleaseReverseAsync(List<INhBackgroundOperationLeaseHandle> leases)
    {
        for (var index = leases.Count - 1; index >= 0; index--)
        {
            await leases[index].DisposeAsync();
        }
    }

    private static string NormalizeResourceKey(string resourceKey)
        => NhBackgroundOperationKeys.HashResourceKey(resourceKey);

    private static TimeSpan ValidateRescheduleDelay(TimeSpan? delay)
    {
        var value = delay ?? TimeSpan.FromSeconds(2);
        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(delay));
        }
        return value;
    }
}

internal sealed class NhBackgroundOperationLeaseHandle : INhBackgroundOperationLeaseHandle
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly NhBackgroundOperationsOptions _options;
    private readonly NhBackgroundOperationAttemptClaim _claim;
    private readonly TimeSpan _leaseDuration;
    private bool _disposed;

    internal NhBackgroundOperationLeaseHandle(
        IServiceScopeFactory scopeFactory,
        NhBackgroundOperationsOptions options,
        NhBackgroundOperationAttemptClaim claim,
        string resourceKey,
        int slot,
        long fencingToken,
        DateTimeOffset expiresAt,
        TimeSpan leaseDuration)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _claim = claim;
        ResourceKey = resourceKey;
        Slot = slot;
        FencingToken = fencingToken;
        ExpiresAt = expiresAt;
        _leaseDuration = leaseDuration;
    }

    public string ResourceKey { get; }
    public int Slot { get; }
    public long FencingToken { get; }
    public DateTimeOffset ExpiresAt { get; private set; }

    public async Task RenewAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(NhBackgroundOperationLeaseHandle));
        }
        await using var scope = _scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRepository<NhBackgroundOperationLease>>();
        await using var transaction = await repository.StartOrGetTransactionScopeAsync(cancellationToken);
        if (!await LockAsync(repository, transaction, cancellationToken))
        {
            throw new NhBackgroundOperationContentionSignal(TimeSpan.FromSeconds(2));
        }

        var lease = await repository.GetAll().SingleAsync(x => x.ResourceKey == ResourceKey && x.Slot == Slot, cancellationToken);
        EnsureOwnership(lease);
        var now = DateTimeOffset.UtcNow;
        lease.HeartbeatAt = now;
        lease.ExpiresAt = now + _leaseDuration;
        lease.Version++;
        await repository.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        ExpiresAt = lease.ExpiresAt.Value;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        await using var scope = _scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRepository<NhBackgroundOperationLease>>();
        await using var transaction = await repository.StartOrGetTransactionScopeAsync(CancellationToken.None);
        if (!await LockAsync(repository, transaction, CancellationToken.None))
        {
            return;
        }
        var lease = await repository.GetAll().SingleOrDefaultAsync(x => x.ResourceKey == ResourceKey && x.Slot == Slot);
        if (lease is null
            || lease.OperationId != _claim.OperationId
            || lease.AttemptId != _claim.AttemptId
            || lease.FencingToken != FencingToken)
        {
            return;
        }

        lease.OperationId = null;
        lease.AttemptId = null;
        lease.HeartbeatAt = DateTimeOffset.UtcNow;
        lease.ExpiresAt = DateTimeOffset.UtcNow;
        lease.Version++;
        await repository.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    private Task<bool> LockAsync(
        IRepository<NhBackgroundOperationLease> repository,
        INhDbTransactionScope transaction,
        CancellationToken cancellationToken)
    {
        return repository.TryAcquireTransactionLockAsync(
            transaction,
            $"NhBackgroundOperation:Lease:{ResourceKey}",
            _options.TransactionLockTimeoutMilliseconds,
            cancellationToken);
    }

    private void EnsureOwnership(NhBackgroundOperationLease lease)
    {
        if (lease.OperationId != _claim.OperationId
            || lease.AttemptId != _claim.AttemptId
            || lease.FencingToken != FencingToken)
        {
            throw new InvalidOperationException("The background-operation lease has been lost to a newer fencing token.");
        }
    }
}

internal sealed class NhBackgroundOperationLeaseSet : INhBackgroundOperationLeaseSet
{
    private readonly IReadOnlyList<INhBackgroundOperationLeaseHandle> _leases;
    private bool _disposed;

    internal NhBackgroundOperationLeaseSet(IReadOnlyList<INhBackgroundOperationLeaseHandle> leases)
    {
        _leases = leases;
    }

    public IReadOnlyList<INhBackgroundOperationLeaseHandle> Leases => _leases;

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        for (var index = _leases.Count - 1; index >= 0; index--)
        {
            await _leases[index].DisposeAsync();
        }
    }
}
