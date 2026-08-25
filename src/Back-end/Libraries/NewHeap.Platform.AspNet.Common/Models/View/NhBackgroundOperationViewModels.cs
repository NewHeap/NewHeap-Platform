using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.Common.Attributes;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AspNet.Common.Models.View;

public sealed class NhBackgroundOperationViewModel
{
    [Filterable, Orderable]
    public Guid Id
    {
        get; set;
    }
    [Filterable, Orderable]
    public DateTimeOffset CreationDateTime
    {
        get; set;
    }
    [Filterable, Orderable]
    public DateTimeOffset LastModifiedDateTime
    {
        get; set;
    }
    [Filterable, Orderable, Searchable]
    public string OperationType { get; set; } = string.Empty;
    public int PayloadSchemaVersion
    {
        get; set;
    }
    [Filterable]
    public Guid OwnerUserId
    {
        get; set;
    }
    [Filterable]
    public Guid? DivisionId
    {
        get; set;
    }
    public Guid? ParentOperationId
    {
        get; set;
    }
    public Guid? RootOperationId
    {
        get; set;
    }
    public string? FanOutKey
    {
        get; set;
    }
    public string? FanOutItemKey
    {
        get; set;
    }
    [Filterable, Orderable]
    public NhBackgroundOperationStatus Status
    {
        get; set;
    }
    public string Queue { get; set; } = string.Empty;
    public int Priority
    {
        get; set;
    }
    public int CurrentAttemptNumber
    {
        get; set;
    }
    public string? DomainObjectType
    {
        get; set;
    }
    public string? DomainObjectId
    {
        get; set;
    }
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
    public string? ProgressPhaseKey
    {
        get; set;
    }
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
    public DateTimeOffset? SensitiveDataRedactedAt
    {
        get; set;
    }
    public string? ResultReferenceType
    {
        get; set;
    }
    public string? ResultReferenceId
    {
        get; set;
    }
    public string? ResultUrl
    {
        get; set;
    }
    public string? FailureCode
    {
        get; set;
    }
    public string? FailureMessageKey
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
    public List<NhBackgroundOperationAttemptViewModel> Attempts { get; set; } = [];
    public List<NhBackgroundOperationStepViewModel> Steps { get; set; } = [];
    public List<NhBackgroundOperationEventViewModel> Events { get; set; } = [];
    public List<NhBackgroundOperationChildViewModel> Children { get; set; } = [];
}

public sealed class NhBackgroundOperationChildViewModel
{
    [Filterable]
    public Guid Id
    {
        get; set;
    }
    public Guid? ParentOperationId
    {
        get; set;
    }
    public string OperationType { get; set; } = string.Empty;
    public string? FanOutKey
    {
        get; set;
    }
    public string? FanOutItemKey
    {
        get; set;
    }
    public NhBackgroundOperationStatus Status
    {
        get; set;
    }
    public decimal? ProgressPercentage
    {
        get; set;
    }
    public DateTimeOffset CreationDateTime
    {
        get; set;
    }
    public DateTimeOffset LastModifiedDateTime
    {
        get; set;
    }
    public DateTimeOffset? CompletedAt
    {
        get; set;
    }
    public string? ResultReferenceType
    {
        get; set;
    }
    public string? ResultReferenceId
    {
        get; set;
    }
    public string? ResultUrl
    {
        get; set;
    }
    public string? FailureCode
    {
        get; set;
    }
    public List<NhBackgroundOperationChildViewModel> Children { get; set; } = [];
}

public sealed class NhBackgroundOperationAttemptViewModel
{
    [Filterable]
    public Guid Id
    {
        get; set;
    }
    public int AttemptNumber
    {
        get; set;
    }
    public NhBackgroundOperationAttemptStatus Status
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
    public string? FailureCode
    {
        get; set;
    }
    public string? RecoveryReason
    {
        get; set;
    }
}

public sealed class NhBackgroundOperationStepViewModel
{
    [Filterable]
    public Guid Id
    {
        get; set;
    }
    public Guid? ParentStepId
    {
        get; set;
    }
    public string StepKey { get; set; } = string.Empty;
    public string? TitleKey
    {
        get; set;
    }
    public string? TitleArgumentsJson
    {
        get; set;
    }
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
    public decimal Weight
    {
        get; set;
    }
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
    public DateTimeOffset? CompletedAt
    {
        get; set;
    }
    public List<NhBackgroundOperationStepViewModel> Children { get; set; } = [];
}

public sealed class NhBackgroundOperationEventViewModel
{
    [Filterable]
    public Guid Id
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
    public DateTimeOffset CreationDateTime
    {
        get; set;
    }
    public string? ResultReferenceType
    {
        get; set;
    }
    public string? ResultReferenceId
    {
        get; set;
    }
    public string? ResultUrl
    {
        get; set;
    }
    public bool IsMilestone
    {
        get; set;
    }
}

public sealed class NhBackgroundOperationCollectionRequestModel : CollectionRequestModel
{
}