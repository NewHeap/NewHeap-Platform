using NewHeap.Platform.AspNet.Common.Models;
using System.ComponentModel.DataAnnotations;

namespace NewHeap.Platform.AspNet.Common.DAL.Entities;

public enum NhBackgroundOperationStatus
{
    PendingDispatch = 0,
    Queued = 10,
    Running = 20,
    WaitingForChildren = 25,
    CancelRequested = 30,
    RetryScheduled = 40,
    Succeeded = 100,
    Failed = 110,
    Cancelled = 120,
    TimedOut = 130
}

public enum NhBackgroundOperationAttemptStatus
{
    Queued = 0,
    Running = 10,
    Suspended = 20,
    Succeeded = 100,
    Failed = 110,
    Cancelled = 120,
    Abandoned = 130
}

public enum NhBackgroundOperationStepStatus
{
    Pending = 0,
    Running = 10,
    Succeeded = 100,
    Failed = 110,
    Skipped = 120,
    Cancelled = 130,
    Indeterminate = 140
}

public enum NhBackgroundOperationAggregationMode
{
    Manual = 0,
    WeightedChildren = 10,
    ItemCount = 20,
    Indeterminate = 30,
    ChildOperations = 40
}

public enum NhBackgroundOperationEventType
{
    StateChanged = 0,
    StepStarted = 10,
    StepProgressed = 20,
    StepCompleted = 30,
    BatchChanged = 40,
    RetryScheduled = 50,
    CancellationRequested = 60,
    Message = 70,
    CheckpointChanged = 80,
    ResultAvailable = 90,
    ChildrenCreated = 100,
    ChildrenCompleted = 110
}

public enum NhBackgroundOperationMessageSeverity
{
    Information = 0,
    Success = 10,
    Warning = 20,
    Error = 30
}

public sealed class NhBackgroundOperation : IdDbEntity
{
    [Key]
    public Guid Id
    {
        get; set;
    }

    public DateTimeOffset CreationDateTime { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastModifiedDateTime { get; set; } = DateTimeOffset.UtcNow;

    [Required, StringLength(200)]
    public string OperationType { get; set; } = string.Empty;

    public int PayloadSchemaVersion { get; set; } = 1;
    [Required]
    public string PayloadJson { get; set; } = "{}";

    public Guid OwnerUserId
    {
        get; set;
    }
    public Guid? DivisionId
    {
        get; set;
    }

    public Guid? ParentOperationId
    {
        get; set;
    }
    public NhBackgroundOperation? ParentOperation
    {
        get; set;
    }
    public List<NhBackgroundOperation> ChildOperations { get; set; } = [];
    public Guid? RootOperationId
    {
        get; set;
    }

    [StringLength(200)]
    public string? FanOutKey
    {
        get; set;
    }

    [StringLength(200)]
    public string? FanOutItemKey
    {
        get; set;
    }

    [Required, StringLength(50)]
    public string ProcessorKey { get; set; } = "default";

    [Required, StringLength(50)]
    public string Queue { get; set; } = "default";

    public int Priority
    {
        get; set;
    }
    public NhBackgroundOperationStatus Status { get; set; } = NhBackgroundOperationStatus.PendingDispatch;
    public int DispatchGeneration
    {
        get; set;
    }

    [StringLength(100)]
    public string? SchedulerJobId
    {
        get; set;
    }

    public int CurrentAttemptNumber
    {
        get; set;
    }
    public Guid? CurrentAttemptId
    {
        get; set;
    }

    [StringLength(450)]
    public string? ConcurrencyKey
    {
        get; set;
    }

    [StringLength(100)]
    public string? DomainObjectType
    {
        get; set;
    }

    [StringLength(200)]
    public string? DomainObjectId
    {
        get; set;
    }

    [StringLength(100)]
    public string? CorrelationId
    {
        get; set;
    }

    public decimal? ProgressCurrent
    {
        get; set;
    }
    public decimal? ProgressTotal
    {
        get; set;
    }
    public decimal? ProgressPercentage
    {
        get; set;
    }

    [StringLength(200)]
    public string? ProgressPhaseKey
    {
        get; set;
    }

    [StringLength(300)]
    public string? ProgressMessageKey
    {
        get; set;
    }
    public string? ProgressMessageArgumentsJson
    {
        get; set;
    }

    public DateTimeOffset? CancelRequestedAt
    {
        get; set;
    }
    public Guid? CancelRequestedByUserId
    {
        get; set;
    }
    public DateTimeOffset? StartedAt
    {
        get; set;
    }
    public DateTimeOffset? HeartbeatAt
    {
        get; set;
    }
    public DateTimeOffset? NextDispatchAt
    {
        get; set;
    }
    public DateTimeOffset? CompletedAt
    {
        get; set;
    }
    public DateTimeOffset? SensitiveDataRedactedAt
    {
        get; set;
    }

    [StringLength(100)]
    public string? ResultReferenceType
    {
        get; set;
    }

    [StringLength(200)]
    public string? ResultReferenceId
    {
        get; set;
    }

    [StringLength(1000)]
    public string? ResultUrl
    {
        get; set;
    }

    [StringLength(100)]
    public string? FailureCode
    {
        get; set;
    }

    [StringLength(300)]
    public string? FailureMessageKey
    {
        get; set;
    }

    [StringLength(100)]
    public string? DiagnosticCorrelationId
    {
        get; set;
    }

    public long Version
    {
        get; set;
    }
    public long LatestEventSequence
    {
        get; set;
    }
    public Guid? UserNotificationId
    {
        get; set;
    }
    public long LastProjectedNotificationEventSequence
    {
        get; set;
    }

    public List<NhBackgroundOperationAttempt> Attempts { get; set; } = [];
    public List<NhBackgroundOperationStep> Steps { get; set; } = [];
    public List<NhBackgroundOperationEvent> Events { get; set; } = [];
    public List<NhBackgroundOperationCheckpoint> Checkpoints { get; set; } = [];
}

public sealed class NhBackgroundOperationAttempt : IdDbEntity
{
    [Key]
    public Guid Id
    {
        get; set;
    }
    public DateTimeOffset CreationDateTime { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastModifiedDateTime { get; set; } = DateTimeOffset.UtcNow;
    public Guid OperationId
    {
        get; set;
    }
    public NhBackgroundOperation? Operation
    {
        get; set;
    }
    public int AttemptNumber
    {
        get; set;
    }
    public int DispatchGeneration
    {
        get; set;
    }

    [StringLength(100)]
    public string? SchedulerJobId
    {
        get; set;
    }

    public NhBackgroundOperationAttemptStatus Status { get; set; } = NhBackgroundOperationAttemptStatus.Queued;

    [StringLength(200)]
    public string? WorkerId
    {
        get; set;
    }

    public DateTimeOffset? StartedAt
    {
        get; set;
    }
    public DateTimeOffset? HeartbeatAt
    {
        get; set;
    }
    public DateTimeOffset? CompletedAt
    {
        get; set;
    }

    [StringLength(100)]
    public string? FailureCode
    {
        get; set;
    }

    [StringLength(100)]
    public string? DiagnosticCorrelationId
    {
        get; set;
    }

    [StringLength(300)]
    public string? RecoveryReason
    {
        get; set;
    }

    public long Version
    {
        get; set;
    }
}

public sealed class NhBackgroundOperationStep : IdDbEntity
{
    [Key]
    public Guid Id
    {
        get; set;
    }
    public DateTimeOffset CreationDateTime { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastModifiedDateTime { get; set; } = DateTimeOffset.UtcNow;
    public Guid OperationId
    {
        get; set;
    }
    public NhBackgroundOperation? Operation
    {
        get; set;
    }
    public Guid? ParentStepId
    {
        get; set;
    }
    public NhBackgroundOperationStep? ParentStep
    {
        get; set;
    }
    public List<NhBackgroundOperationStep> Children { get; set; } = [];

    [Required, StringLength(200)]
    public string StepKey { get; set; } = string.Empty;

    [StringLength(300)]
    public string? TitleKey
    {
        get; set;
    }
    public string? TitleArgumentsJson
    {
        get; set;
    }

    [StringLength(300)]
    public string? MessageKey
    {
        get; set;
    }
    public string? MessageArgumentsJson
    {
        get; set;
    }

    public NhBackgroundOperationStepStatus Status
    {
        get; set;
    }
    public NhBackgroundOperationAggregationMode AggregationMode
    {
        get; set;
    }
    public decimal Weight { get; set; } = 1;
    public decimal? Current
    {
        get; set;
    }
    public decimal? Total
    {
        get; set;
    }
    public decimal? Percentage
    {
        get; set;
    }

    public long DiscoveredItems
    {
        get; set;
    }
    public long ProcessedItems
    {
        get; set;
    }
    public long SucceededItems
    {
        get; set;
    }
    public long FailedItems
    {
        get; set;
    }
    public long SkippedItems
    {
        get; set;
    }
    public long RetriedItems
    {
        get; set;
    }
    public long ActiveItems
    {
        get; set;
    }
    public bool ContinueOnChildFailure
    {
        get; set;
    }

    public int DisplayOrder
    {
        get; set;
    }
    public int Depth
    {
        get; set;
    }
    public DateTimeOffset? StartedAt
    {
        get; set;
    }
    public DateTimeOffset? HeartbeatAt
    {
        get; set;
    }
    public DateTimeOffset? CompletedAt
    {
        get; set;
    }
    public Guid? CurrentAttemptId
    {
        get; set;
    }
    public long FencingVersion
    {
        get; set;
    }
    public long Version
    {
        get; set;
    }
}

public sealed class NhBackgroundOperationEvent : IdDbEntity
{
    [Key]
    public Guid Id
    {
        get; set;
    }
    public Guid OperationId
    {
        get; set;
    }
    public NhBackgroundOperation? Operation
    {
        get; set;
    }
    public long Sequence
    {
        get; set;
    }
    public Guid? StepId
    {
        get; set;
    }

    [StringLength(200)]
    public string? StepKey
    {
        get; set;
    }

    public NhBackgroundOperationEventType EventType
    {
        get; set;
    }
    public NhBackgroundOperationMessageSeverity Severity
    {
        get; set;
    }

    [StringLength(300)]
    public string? MessageKey
    {
        get; set;
    }
    public string? MessageArgumentsJson
    {
        get; set;
    }
    public long SnapshotVersion
    {
        get; set;
    }
    public DateTimeOffset CreationDateTime { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastModifiedDateTime { get; set; } = DateTimeOffset.UtcNow;

    [StringLength(100)]
    public string? ResultReferenceType
    {
        get; set;
    }

    [StringLength(200)]
    public string? ResultReferenceId
    {
        get; set;
    }

    [StringLength(1000)]
    public string? ResultUrl
    {
        get; set;
    }
    public Guid? AttemptId
    {
        get; set;
    }
    public bool IsMilestone
    {
        get; set;
    }
    public bool IsOperatorOnly
    {
        get; set;
    }
}

public sealed class NhBackgroundOperationCheckpoint
{
    public Guid OperationId
    {
        get; set;
    }
    public NhBackgroundOperation? Operation
    {
        get; set;
    }

    [Required, StringLength(200)]
    public string CheckpointKey { get; set; } = string.Empty;

    public int SchemaVersion { get; set; } = 1;
    [Required]
    public string ValueJson { get; set; } = "{}";
    public Guid? AttemptId
    {
        get; set;
    }
    public DateTimeOffset CreationDateTime { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastModifiedDateTime { get; set; } = DateTimeOffset.UtcNow;
    public long Version
    {
        get; set;
    }
}

public sealed class NhBackgroundOperationIdempotencyRecord
{
    [Required, StringLength(100)]
    public string Scope { get; set; } = string.Empty;

    [Required, StringLength(64)]
    public string KeyHash { get; set; } = string.Empty;

    public Guid OperationId
    {
        get; set;
    }
    public NhBackgroundOperation? Operation
    {
        get; set;
    }
    public DateTimeOffset CreationDateTime { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAt
    {
        get; set;
    }
}

public sealed class NhBackgroundOperationLease
{
    [Required, StringLength(450)]
    public string ResourceKey { get; set; } = string.Empty;
    public int Slot
    {
        get; set;
    }
    public Guid? OperationId
    {
        get; set;
    }
    public NhBackgroundOperation? Operation
    {
        get; set;
    }
    public Guid? AttemptId
    {
        get; set;
    }
    public DateTimeOffset? AcquiredAt
    {
        get; set;
    }
    public DateTimeOffset? HeartbeatAt
    {
        get; set;
    }
    public DateTimeOffset? ExpiresAt
    {
        get; set;
    }
    public long FencingToken
    {
        get; set;
    }
    public long Version
    {
        get; set;
    }
}