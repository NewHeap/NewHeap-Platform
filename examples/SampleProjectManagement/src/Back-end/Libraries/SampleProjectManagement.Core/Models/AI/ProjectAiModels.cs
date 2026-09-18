namespace SampleProjectManagement.Core.Models.AI;

using SampleProjectManagement.DAL.Entities;

public sealed record ProjectAiSearchInput(string? Query, int Limit = 10);

public sealed record ProjectAiSearchItem(Guid Id, string Key, string Name);

public sealed record ProjectAiApprovalItem(
    Guid Id,
    string Key,
    string Name,
    ProjectStatus Status);

public sealed record ProjectAiStatusChangeInput(
    Guid ProjectId,
    ProjectStatus Status);

public sealed record ProjectAiStatusChangeReport(
    Guid ProjectId,
    ProjectStatus PreviousStatus,
    ProjectStatus CurrentStatus,
    bool Accepted);

/// <summary>
/// Flat wire input of the consumer-authoritative status receipt tool. The approval
/// grant and idempotency key are ordinary domain arguments: the application validates
/// them itself inside the governed invocation.
/// </summary>
public sealed record ProjectAiStatusReceiptRequest(
    Guid ProjectId,
    ProjectStatus Status,
    string ApprovalGrant,
    string IdempotencyKey);

/// <summary>
/// Domain receipt returned for every status receipt request, including expected
/// approval denials, so remote callers always receive the same typed wire contract.
/// </summary>
public sealed record ProjectAiStatusReceipt(
    Guid ProjectId,
    string Execution,
    string DatabaseCompletion,
    string Code,
    bool IdempotentReplay,
    string EvidenceReference,
    DateTimeOffset? CommittedAt)
{
    public const string ExecutedExecution = "executed";
    public const string DenyExecution = "deny";
    public const string CommittedCompletion = "committed";
    public const string NotExecutedCompletion = "not-executed";
    public const string StatusUpdatedCode = "status-updated";
    public const string ApprovalInvalidCode = "approval-invalid-expired-or-replayed";
}

/// <summary>
/// Flat wire input of the approval-issuing tool.
/// </summary>
public sealed record ProjectAiStatusApprovalRequest(
    Guid ProjectId,
    ProjectStatus Status);

/// <summary>
/// A single-use approval grant for exactly one project status change. The grant value is
/// the grant id that the consuming status receipt request presents.
/// </summary>
public sealed record ProjectAiStatusApprovalGrant(
    Guid ProjectId,
    ProjectStatus Status,
    string ApprovalGrant,
    DateTimeOffset ExpiresAt,
    string EvidenceReference);

public sealed record ProjectAiContextDocument(
    Guid ProjectId,
    string Key,
    string Name,
    string? Description,
    DateTimeOffset LastModifiedAt);
