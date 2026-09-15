using System.Collections.Concurrent;
using NewHeap.Platform.AI;
using NewHeap.Platform.Common.Models;
using SampleProjectManagement.Core.Models.AI;
using SampleProjectManagement.DAL.Entities;

namespace SampleProjectManagement.Core.Services;

/// <summary>
/// Consumer-authoritative approval and idempotency for the status receipt tools. The
/// application issues single-use approval grants, burns a grant on every presentation, and
/// reconciles replays of the same idempotency key by returning the stored receipt.
/// </summary>
public interface IProjectAiStatusReceiptService
{
    ProjectAiStatusApprovalGrant IssueApprovalGrant(
        Guid divisionId,
        Guid projectId,
        ProjectStatus status,
        DateTimeOffset expiresAt);

    Task<ProjectAiStatusReceipt> ApplyAsync(
        Guid divisionId,
        ProjectAiStatusReceiptRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// In-memory grant and receipt store. It is executable sample evidence, not durable
/// production storage; a real application persists grants and receipts transactionally.
/// </summary>
public sealed class ProjectAiStatusReceiptStore
{
    private readonly ConcurrentDictionary<string, ApprovalGrant> _grants = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ProjectAiStatusReceipt> _receipts = new(StringComparer.Ordinal);

    public static string ReceiptKey(Guid divisionId, string idempotencyKey)
    {
        return $"{divisionId:N}:{idempotencyKey}";
    }

    /// <summary>
    /// A bounded evidence reference that names the presented grant without echoing
    /// arbitrary caller input.
    /// </summary>
    public static string EvidenceReference(string? grant)
    {
        var isGrantId = grant is { Length: 32 } && grant.All(char.IsAsciiHexDigitLower);
        return isGrantId
            ? $"project-status-grant:{grant}"
            : "project-status-grant:unrecognized";
    }

    public string IssueGrant(
        Guid divisionId,
        Guid projectId,
        ProjectStatus status,
        DateTimeOffset expiresAt)
    {
        var token = Guid.NewGuid().ToString("N");
        _grants[token] = new ApprovalGrant(divisionId, projectId, status, expiresAt);
        return token;
    }

    public bool TryConsumeGrant(
        string token,
        Guid divisionId,
        Guid projectId,
        ProjectStatus status,
        DateTimeOffset now)
    {
        // Any presentation burns the grant, including a mismatched or expired one.
        if (!_grants.TryRemove(token, out var grant))
        {
            return false;
        }
        return grant.DivisionId == divisionId
            && grant.ProjectId == projectId
            && grant.Status == status
            && grant.ExpiresAt > now;
    }

    public bool TryGetReceipt(string key, out ProjectAiStatusReceipt receipt)
    {
        return _receipts.TryGetValue(key, out receipt!);
    }

    public ProjectAiStatusReceipt StoreReceipt(string key, ProjectAiStatusReceipt receipt)
    {
        return _receipts.GetOrAdd(key, receipt);
    }

    private sealed record ApprovalGrant(
        Guid DivisionId,
        Guid ProjectId,
        ProjectStatus Status,
        DateTimeOffset ExpiresAt);
}

/// <summary>
/// Validates the presented approval grant before the consuming tool runs. A replayed
/// idempotency key passes untouched so the engine can return its reconciled receipt; an
/// invalid, expired or replayed grant is burned and returned as the typed deny receipt.
/// </summary>
public sealed class ProjectAiStatusApprovalEvidenceValidator(
    ProjectAiStatusReceiptStore store) : INhAiAuthoritativeExecutionEvidenceValidator
{
    public ValueTask<TaskResult<NhAiAuthoritativeExecutionEvidence>> ValidateAsync(
        NhAiToolDescriptor descriptor,
        NhAiInvocationContext context,
        object arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(descriptor.Id, ProjectAiTools.ApplyStatusReceiptToolId, StringComparison.Ordinal)
            || arguments is not ProjectAiStatusReceiptRequest request)
        {
            return ValueTask.FromResult(
                TaskResult<NhAiAuthoritativeExecutionEvidence>.Succeeded(
                    NhAiAuthoritativeExecutionEvidence.None));
        }

        if (!context.TryGetScopeValue(ProjectAiTools.DivisionScopeKey, out var divisionValue)
            || !Guid.TryParse(divisionValue, out var divisionId))
        {
            return ValueTask.FromResult(
                TaskResult<NhAiAuthoritativeExecutionEvidence>.Failed(
                    "project-division-scope-missing",
                    "The authorized AI invocation context has no active division."));
        }

        var receiptKey = ProjectAiStatusReceiptStore.ReceiptKey(divisionId, request.IdempotencyKey);
        if (store.TryGetReceipt(receiptKey, out _))
        {
            // Consumer-authoritative idempotency: a replay reaches the engine.
            return ValueTask.FromResult(
                TaskResult<NhAiAuthoritativeExecutionEvidence>.Succeeded(
                    NhAiAuthoritativeExecutionEvidence.None));
        }

        var evidenceReference = ProjectAiStatusReceiptStore.EvidenceReference(request.ApprovalGrant);
        var approved = store.TryConsumeGrant(
            request.ApprovalGrant ?? string.Empty,
            divisionId,
            request.ProjectId,
            request.Status,
            DateTimeOffset.UtcNow);
        if (!approved)
        {
            var denial = store.StoreReceipt(
                receiptKey,
                new ProjectAiStatusReceipt(
                    request.ProjectId,
                    ProjectAiStatusReceipt.DenyExecution,
                    ProjectAiStatusReceipt.NotExecutedCompletion,
                    ProjectAiStatusReceipt.ApprovalInvalidCode,
                    false,
                    evidenceReference,
                    null));
            return ValueTask.FromResult(
                TaskResult<NhAiAuthoritativeExecutionEvidence>
                    .Failed(
                        ProjectAiStatusReceipt.ApprovalInvalidCode,
                        "The approval grant is invalid, expired or replayed.")
                    .WithData(NhAiAuthoritativeExecutionEvidence.Denied(denial, evidenceReference)));
        }

        return ValueTask.FromResult(
            TaskResult<NhAiAuthoritativeExecutionEvidence>.Succeeded(
                new NhAiAuthoritativeExecutionEvidence(true, false, null, evidenceReference)));
    }
}

public sealed class ProjectAiStatusReceiptService(
    ProjectAiStatusReceiptStore store,
    IProjectAiMutationService projectMutationService) : IProjectAiStatusReceiptService
{
    public ProjectAiStatusApprovalGrant IssueApprovalGrant(
        Guid divisionId,
        Guid projectId,
        ProjectStatus status,
        DateTimeOffset expiresAt)
    {
        var grant = store.IssueGrant(divisionId, projectId, status, expiresAt);
        return new ProjectAiStatusApprovalGrant(
            projectId,
            status,
            grant,
            expiresAt,
            ProjectAiStatusReceiptStore.EvidenceReference(grant));
    }

    /// <summary>
    /// Applies an approved status change. The approval grant has already been consumed by
    /// <see cref="ProjectAiStatusApprovalEvidenceValidator"/>; a replayed key returns the
    /// stored receipt, including a stored denial.
    /// </summary>
    public async Task<ProjectAiStatusReceipt> ApplyAsync(
        Guid divisionId,
        ProjectAiStatusReceiptRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.IdempotencyKey);
        cancellationToken.ThrowIfCancellationRequested();

        var receiptKey = ProjectAiStatusReceiptStore.ReceiptKey(divisionId, request.IdempotencyKey);
        if (store.TryGetReceipt(receiptKey, out var existing))
        {
            return existing with { IdempotentReplay = true };
        }

        var evidenceReference = ProjectAiStatusReceiptStore.EvidenceReference(request.ApprovalGrant);
        var mutation = await projectMutationService.ChangeStatusForAiAsync(
            divisionId,
            request.ProjectId,
            request.Status,
            cancellationToken);
        if (!mutation.Success)
        {
            return store.StoreReceipt(
                receiptKey,
                new ProjectAiStatusReceipt(
                    request.ProjectId,
                    ProjectAiStatusReceipt.DenyExecution,
                    ProjectAiStatusReceipt.NotExecutedCompletion,
                    "project-status-rejected",
                    false,
                    evidenceReference,
                    null));
        }

        return store.StoreReceipt(
            receiptKey,
            new ProjectAiStatusReceipt(
                request.ProjectId,
                ProjectAiStatusReceipt.ExecutedExecution,
                ProjectAiStatusReceipt.CommittedCompletion,
                ProjectAiStatusReceipt.StatusUpdatedCode,
                false,
                evidenceReference,
                DateTimeOffset.UtcNow));
    }
}
