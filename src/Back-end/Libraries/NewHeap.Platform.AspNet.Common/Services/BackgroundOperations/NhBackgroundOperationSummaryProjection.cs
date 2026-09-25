using NewHeap.Platform.AspNet.Common.DAL.Entities;

namespace NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;

internal static class NhBackgroundOperationSummaryProjection
{
    /// <summary>
    /// Selects the columns that operation summaries return. Collection pages never
    /// include the request payload, which can hold up to
    /// <see cref="NhBackgroundOperationsOptions.MaxPayloadBytes"/> per row. Apply it after
    /// filtering, ordering and paging.
    /// </summary>
    public static IQueryable<NhBackgroundOperation> SelectSummary(this IQueryable<NhBackgroundOperation> query)
    {
        return query.Select(x => new NhBackgroundOperation
        {
            Id = x.Id,
            CreationDateTime = x.CreationDateTime,
            LastModifiedDateTime = x.LastModifiedDateTime,
            OperationType = x.OperationType,
            PayloadSchemaVersion = x.PayloadSchemaVersion,
            OwnerUserId = x.OwnerUserId,
            DivisionId = x.DivisionId,
            ParentOperationId = x.ParentOperationId,
            RootOperationId = x.RootOperationId,
            FanOutKey = x.FanOutKey,
            FanOutItemKey = x.FanOutItemKey,
            ProcessorKey = x.ProcessorKey,
            Queue = x.Queue,
            Priority = x.Priority,
            Status = x.Status,
            DispatchGeneration = x.DispatchGeneration,
            SchedulerJobId = x.SchedulerJobId,
            CurrentAttemptNumber = x.CurrentAttemptNumber,
            DomainObjectType = x.DomainObjectType,
            DomainObjectId = x.DomainObjectId,
            CorrelationId = x.CorrelationId,
            ProgressCurrent = x.ProgressCurrent,
            ProgressTotal = x.ProgressTotal,
            ProgressPercentage = x.ProgressPercentage,
            ProgressPhaseKey = x.ProgressPhaseKey,
            ProgressMessageKey = x.ProgressMessageKey,
            ProgressMessageArgumentsJson = x.ProgressMessageArgumentsJson,
            CancelRequestedAt = x.CancelRequestedAt,
            CancelRequestedByUserId = x.CancelRequestedByUserId,
            StartedAt = x.StartedAt,
            HeartbeatAt = x.HeartbeatAt,
            NextDispatchAt = x.NextDispatchAt,
            CompletedAt = x.CompletedAt,
            SensitiveDataRedactedAt = x.SensitiveDataRedactedAt,
            ResultReferenceType = x.ResultReferenceType,
            ResultReferenceId = x.ResultReferenceId,
            ResultUrl = x.ResultUrl,
            FailureCode = x.FailureCode,
            FailureMessageKey = x.FailureMessageKey,
            DiagnosticCorrelationId = x.DiagnosticCorrelationId,
            Version = x.Version,
            LatestEventSequence = x.LatestEventSequence
        });
    }
}
