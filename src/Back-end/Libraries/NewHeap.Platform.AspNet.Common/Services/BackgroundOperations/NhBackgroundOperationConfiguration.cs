using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.Common.Utilities;

namespace NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;

public enum NhBackgroundOperationConflictBehavior
{
    Queue = 0,
    ReturnExisting = 10,
    Reject = 20,
    Reschedule = 30
}

public enum NhBackgroundOperationIdempotency
{
    NaturallyIdempotent = 0,
    IdempotentWithKey = 10,
    NonIdempotent = 20
}

public sealed class NhBackgroundOperationsOptions
{
    public string ProcessorKey { get; set; } = "default";
    public bool DispatchWorkersEnabled { get; set; } = true;
    public bool ReconciliationEnabled { get; set; } = true;
    public bool CleanupEnabled { get; set; } = true;
    public bool LiveUpdatesEnabled { get; set; } = true;
    public string HubPath { get; set; } = "/hub/background-operations";
    public bool UserNotificationProjectionEnabled { get; set; } = true;
    public string OperationUrlPrefix { get; set; } = "/background-operations";
    public TimeSpan DispatchInterval { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan ReconciliationInterval { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromHours(1);
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan StaleAttemptTimeout { get; set; } = TimeSpan.FromMinutes(2);
    public TimeSpan DefaultSoftTimeout { get; set; } = TimeSpan.FromHours(1);
    public TimeSpan DefaultLeaseDuration { get; set; } = TimeSpan.FromMinutes(2);
    public TimeSpan ProgressFlushInterval { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan PayloadRetentionPeriod { get; set; } = TimeSpan.FromDays(7);
    public TimeSpan EventRetentionPeriod { get; set; } = TimeSpan.FromDays(7);
    public TimeSpan SucceededRetentionPeriod { get; set; } = TimeSpan.FromDays(30);
    public TimeSpan CancelledRetentionPeriod { get; set; } = TimeSpan.FromDays(30);
    public TimeSpan FailedRetentionPeriod { get; set; } = TimeSpan.FromDays(90);
    public int TransactionLockTimeoutMilliseconds { get; set; } = 5_000;
    public int DispatchBatchSize { get; set; } = 25;
    public int ReconciliationBatchSize { get; set; } = 50;
    public int MaxConcurrentOperations { get; set; } = int.MaxValue;
    public IDictionary<string, int> QueueConcurrencyLimits { get; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    public int MaxPayloadBytes { get; set; } = 64 * 1024;
    public int MaxCheckpointBytes { get; set; } = 16 * 1024;
    public int MaxMessageArgumentsBytes { get; set; } = 8 * 1024;
    /// <summary>
    /// Target maximum for retained operation events. Unprojected notification
    /// milestones are protected and may temporarily exceed this target until
    /// notification reconciliation succeeds.
    /// </summary>
    public int MaxEventsPerOperation { get; set; } = 500;
    public int MaxFanOutChildren { get; set; } = 1_000;
    public int CleanupBatchSize { get; set; } = 100;
    public int DefaultRetryCount { get; set; } = 3;

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(ProcessorKey))
        {
            throw new InvalidOperationException("Background operation ProcessorKey is required.");
        }

        if (LiveUpdatesEnabled
            && !IsApplicationPath(HubPath, "/hub"))
        {
            throw new InvalidOperationException("Background operation HubPath must be under /hub so the configured bearer-token transport applies.");
        }

        if (UserNotificationProjectionEnabled
            && !IsApplicationPath(OperationUrlPrefix))
        {
            throw new InvalidOperationException("Background operation OperationUrlPrefix must be an absolute application path.");
        }

        if (DispatchInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Background operation DispatchInterval must be positive.");
        }

        if (ReconciliationInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Background operation ReconciliationInterval must be positive because it also bounds fan-out wake-up recovery.");
        }

        if (CleanupEnabled && CleanupInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Background operation CleanupInterval must be positive when cleanup is enabled.");
        }

        if (HeartbeatInterval <= TimeSpan.Zero || StaleAttemptTimeout <= HeartbeatInterval)
        {
            throw new InvalidOperationException("StaleAttemptTimeout must be greater than HeartbeatInterval.");
        }

        if (DefaultLeaseDuration <= HeartbeatInterval)
        {
            throw new InvalidOperationException("DefaultLeaseDuration must be greater than HeartbeatInterval.");
        }

        if (DefaultSoftTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Background operation DefaultSoftTimeout must be positive.");
        }

        if (ProgressFlushInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Background operation ProgressFlushInterval must be positive.");
        }

        if (TransactionLockTimeoutMilliseconds < 1)
        {
            throw new InvalidOperationException("Background operation TransactionLockTimeoutMilliseconds must be positive.");
        }

        if (MaxPayloadBytes < 1 || MaxCheckpointBytes < 1 || MaxMessageArgumentsBytes < 1)
        {
            throw new InvalidOperationException("Background operation persistence size limits must be positive.");
        }

        if (MaxEventsPerOperation < 1
            || MaxFanOutChildren < 1
            || DefaultRetryCount < 0
            || CleanupBatchSize < 1
            || DispatchBatchSize < 1
            || ReconciliationBatchSize < 1
            || MaxConcurrentOperations < 1
            || QueueConcurrencyLimits.Any(x => string.IsNullOrWhiteSpace(x.Key) || x.Value < 1))
        {
            throw new InvalidOperationException("Background operation retention and retry values are invalid.");
        }

        if (PayloadRetentionPeriod < TimeSpan.Zero
            || EventRetentionPeriod < TimeSpan.Zero
            || SucceededRetentionPeriod <= TimeSpan.Zero
            || CancelledRetentionPeriod <= TimeSpan.Zero
            || FailedRetentionPeriod <= TimeSpan.Zero
            || PayloadRetentionPeriod > SucceededRetentionPeriod
            || PayloadRetentionPeriod > CancelledRetentionPeriod
            || PayloadRetentionPeriod > FailedRetentionPeriod)
        {
            throw new InvalidOperationException("Background operation retention periods are invalid.");
        }
    }

    private static bool IsApplicationPath(string? value, string? requiredRoot = null)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !value.StartsWith('/')
            || value.StartsWith("//", StringComparison.Ordinal)
            || value.Contains('\\')
            || value.Contains('?')
            || value.Contains('#'))
        {
            return false;
        }

        if (requiredRoot is null)
        {
            return true;
        }

        return string.Equals(value, requiredRoot, StringComparison.OrdinalIgnoreCase)
               || value.StartsWith($"{requiredRoot}/", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class NhBackgroundOperationEnqueueOptions
{
    public required Guid OwnerUserId { get; init; }
    public Guid? DivisionId { get; init; }
    public string? IdempotencyKey { get; init; }
    public string? IdempotencyScope { get; init; }
    public string? ConcurrencyKey { get; init; }
    public NhBackgroundOperationConflictBehavior? ConflictBehavior { get; init; }
    public int Priority { get; init; }
    public string? DomainObjectType { get; init; }
    public string? DomainObjectId { get; init; }
    public string? CorrelationId { get; init; }
}

public sealed class NhBackgroundOperationLeaseOptions
{
    public int Slots { get; init; } = 1;
    public TimeSpan? LeaseDuration { get; init; }
    public TimeSpan WaitTimeout { get; init; } = TimeSpan.Zero;
}

public sealed class NhBackgroundOperationBatchOptions
{
    public int FlushEveryItems { get; init; } = 25;
    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromSeconds(1);
    public bool ContinueOnItemFailure { get; init; }
    public int MaximumToleratedFailures { get; init; }
}

public sealed class NhBackgroundOperationDescriptor
{
    internal Type RequestType { get; init; } = null!;
    internal Type HandlerType { get; init; } = null!;
    public required string OperationType { get; init; }
    public int PayloadSchemaVersion { get; internal set; } = 1;
    public string? Queue { get; internal set; }
    public int RetryCount { get; internal set; }
    public TimeSpan? SoftTimeout { get; internal set; }
    public int MaxConcurrency { get; internal set; } = int.MaxValue;
    public NhBackgroundOperationIdempotency Idempotency { get; internal set; } = NhBackgroundOperationIdempotency.NaturallyIdempotent;
    public NhBackgroundOperationConflictBehavior ConflictBehavior { get; internal set; } = NhBackgroundOperationConflictBehavior.Queue;
    internal Func<object, string?>? ConcurrencyKeyFactory { get; set; }
}

public sealed class NhBackgroundOperationDefinitionBuilder<TRequest>
{
    private readonly NhBackgroundOperationDescriptor _descriptor;

    internal NhBackgroundOperationDefinitionBuilder(NhBackgroundOperationDescriptor descriptor)
    {
        _descriptor = descriptor;
    }

    public NhBackgroundOperationDefinitionBuilder<TRequest> UseQueue(string queue)
    {
        _descriptor.Queue = NhBackgroundOperationKeys.NormalizeQueueName(queue);
        return this;
    }

    public NhBackgroundOperationDefinitionBuilder<TRequest> WithRetry(int retryCount)
    {
        if (retryCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(retryCount));
        }
        _descriptor.RetryCount = retryCount;
        return this;
    }

    public NhBackgroundOperationDefinitionBuilder<TRequest> WithPayloadSchemaVersion(int schemaVersion)
    {
        if (schemaVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        }
        _descriptor.PayloadSchemaVersion = schemaVersion;
        return this;
    }

    public NhBackgroundOperationDefinitionBuilder<TRequest> WithSoftTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
        _descriptor.SoftTimeout = timeout;
        return this;
    }

    public NhBackgroundOperationDefinitionBuilder<TRequest> WithTypeConcurrency(int maxConcurrency)
    {
        if (maxConcurrency < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConcurrency));
        }
        _descriptor.MaxConcurrency = maxConcurrency;
        return this;
    }

    public NhBackgroundOperationDefinitionBuilder<TRequest> ExclusivePer(
        Func<TRequest, string> resourceKey,
        NhBackgroundOperationConflictBehavior conflictBehavior = NhBackgroundOperationConflictBehavior.ReturnExisting)
    {
        ArgumentNullException.ThrowIfNull(resourceKey);
        _descriptor.ConcurrencyKeyFactory = request => resourceKey((TRequest)request);
        _descriptor.ConflictBehavior = conflictBehavior;
        return this;
    }

    public NhBackgroundOperationDefinitionBuilder<TRequest> RequireIdempotency(NhBackgroundOperationIdempotency idempotency)
    {
        _descriptor.Idempotency = idempotency;
        return this;
    }
}

public sealed class NhBackgroundOperationRegistry
{
    private readonly IReadOnlyDictionary<Type, NhBackgroundOperationDescriptor> _byRequestType;
    private readonly IReadOnlyDictionary<string, NhBackgroundOperationDescriptor> _byOperationType;

    internal NhBackgroundOperationRegistry(IEnumerable<NhBackgroundOperationDescriptor> descriptors)
    {
        var values = descriptors.ToArray();
        _byRequestType = values.ToDictionary(x => x.RequestType);
        _byOperationType = values.ToDictionary(x => x.OperationType, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<NhBackgroundOperationDescriptor> Descriptors => _byRequestType.Values.ToArray();

    public bool TryGetForRequest(Type requestType, out NhBackgroundOperationDescriptor descriptor)
    {
        return _byRequestType.TryGetValue(requestType, out descriptor!);
    }

    public NhBackgroundOperationDescriptor GetForRequest(Type requestType)
    {
        return _byRequestType.TryGetValue(requestType, out var descriptor)
            ? descriptor
            : throw new InvalidOperationException($"No background operation is registered for request type '{requestType.Name}'.");
    }

    public bool TryGetForOperationType(
        string operationType,
        out NhBackgroundOperationDescriptor descriptor)
    {
        return _byOperationType.TryGetValue(operationType, out descriptor!);
    }

    public NhBackgroundOperationDescriptor GetForOperationType(string operationType)
    {
        return _byOperationType.TryGetValue(operationType, out var descriptor)
            ? descriptor
            : throw new InvalidOperationException($"No background operation handler is registered for '{operationType}'.");
    }
}

public sealed class NhBackgroundOperationBuilder
{
    private readonly IServiceCollection _services;
    private readonly NhBackgroundOperationsOptions _options;
    private readonly List<NhBackgroundOperationDescriptor> _descriptors = [];

    internal NhBackgroundOperationBuilder(IServiceCollection services, NhBackgroundOperationsOptions options)
    {
        _services = services;
        _options = options;
    }

    public NhBackgroundOperationsOptions Options => _options;

    public NhBackgroundOperationBuilder WithGlobalConcurrency(int maxConcurrentOperations)
    {
        if (maxConcurrentOperations < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConcurrentOperations));
        }
        _options.MaxConcurrentOperations = maxConcurrentOperations;
        return this;
    }

    public NhBackgroundOperationBuilder WithQueueConcurrency(string queue, int maxConcurrentOperations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queue);
        if (maxConcurrentOperations < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConcurrentOperations));
        }
        _options.QueueConcurrencyLimits[NhHangfireUtil.GetQueueName(queue)] = maxConcurrentOperations;
        return this;
    }

    public NhBackgroundOperationBuilder WithDefaultQueueConcurrency(int maxConcurrentOperations)
    {
        if (maxConcurrentOperations < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConcurrentOperations));
        }
        _options.QueueConcurrencyLimits[NhHangfireUtil.GetQueueName()] = maxConcurrentOperations;
        return this;
    }

    public NhBackgroundOperationBuilder Add<TRequest, THandler>(
        string operationType,
        Action<NhBackgroundOperationDefinitionBuilder<TRequest>>? configure = null)
        where THandler : class, INhBackgroundOperationHandler<TRequest>
    {
        NhBackgroundOperationKeys.ValidateOperationType(operationType);
        if (_descriptors.Any(x => x.RequestType == typeof(TRequest) || string.Equals(x.OperationType, operationType, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Background operation '{operationType}' or request type '{typeof(TRequest).Name}' is already registered.");
        }

        var descriptor = new NhBackgroundOperationDescriptor
        {
            RequestType = typeof(TRequest),
            HandlerType = typeof(THandler),
            OperationType = operationType.Trim(),
            RetryCount = _options.DefaultRetryCount
        };
        configure?.Invoke(new NhBackgroundOperationDefinitionBuilder<TRequest>(descriptor));
        if (descriptor.Idempotency == NhBackgroundOperationIdempotency.NonIdempotent && descriptor.RetryCount > 0)
        {
            throw new InvalidOperationException($"Non-idempotent operation '{operationType}' cannot enable automatic retries.");
        }

        _services.TryAddScoped<THandler>();
        _descriptors.Add(descriptor);
        return this;
    }

    internal NhBackgroundOperationRegistry Build()
    {
        _options.Validate();
        if (_descriptors.Count == 0 && _options.DispatchWorkersEnabled)
        {
            throw new InvalidOperationException("At least one background operation handler must be registered.");
        }

        return new NhBackgroundOperationRegistry(_descriptors);
    }
}

public sealed class NhBackgroundOperationProgressPlanBuilder
{
    internal List<NhBackgroundOperationProgressPlanStep> Steps { get; } = [];

    public NhBackgroundOperationProgressPlanBuilder Step(string key, decimal weight, string titleKey)
    {
        NhBackgroundOperationKeys.ValidateStepKey(key);
        if (weight < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(weight));
        }

        if (Steps.Any(x => x.Key == key))
        {
            throw new InvalidOperationException($"Progress step '{key}' is already defined.");
        }
        Steps.Add(new NhBackgroundOperationProgressPlanStep(key, weight, titleKey, Steps.Count));
        return this;
    }
}

internal sealed record NhBackgroundOperationProgressPlanStep(string Key, decimal Weight, string TitleKey, int DisplayOrder);
