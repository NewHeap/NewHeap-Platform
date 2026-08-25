using NewHeap.Platform.AspNet.Common.DAL.Entities;

namespace NewHeap.Platform.AspNet.Common.Models.Mutate;

/// <summary>
/// Internal mutation shape used while creating an operation. The generic HTTP API
/// does not accept this model; domain endpoints enqueue registered request types.
/// </summary>
public sealed class NhBackgroundOperationMutateModel
{
    public string OperationType { get; set; } = string.Empty;
    public int PayloadSchemaVersion { get; set; } = 1;
    public string PayloadJson { get; set; } = "{}";
    public Guid OwnerUserId
    {
        get; set;
    }
    public Guid? DivisionId
    {
        get; set;
    }
    public string ProcessorKey { get; set; } = "default";
    public string Queue { get; set; } = "default";
    public int Priority
    {
        get; set;
    }
    public NhBackgroundOperationStatus Status { get; set; } = NhBackgroundOperationStatus.PendingDispatch;
    public string? ConcurrencyKey
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
}