using System.Collections.Concurrent;
using SampleProjectManagement.Core.Models.AI;
using SampleProjectManagement.DAL.Entities;

namespace SampleProjectManagement.Core.Services;

/// <summary>
/// Consumer-authoritative approval and idempotency for the status receipt tool. The
/// application issues single-use approval grants, burns a grant on every mismatch,
/// and reconciles replays of the same idempotency key by returning the stored receipt.
/// </summary>
public interface IProjectAiStatusReceiptService
{
    string IssueApprovalGrant(
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
        // Any use burns the grant, including a mismatched or expired one.
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

public sealed class ProjectAiStatusReceiptService(
    ProjectAiStatusReceiptStore store,
    IProjectAiMutationService projectMutationService) : IProjectAiStatusReceiptService
{
    public string IssueApprovalGrant(
        Guid divisionId,
        Guid projectId,
        ProjectStatus status,
        DateTimeOffset expiresAt)
    {
        return store.IssueGrant(divisionId, projectId, status, expiresAt);
    }

    public async Task<ProjectAiStatusReceipt> ApplyAsync(
        Guid divisionId,
        ProjectAiStatusReceiptRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.IdempotencyKey);
        cancellationToken.ThrowIfCancellationRequested();

        var receiptKey = $"{divisionId:N}:{request.IdempotencyKey}";
        if (store.TryGetReceipt(receiptKey, out var existing))
        {
            // A replayed key reaches the engine and returns the reconciled receipt.
            return existing with { IdempotentReplay = true };
        }

        var evidenceReference = $"project-status-grant:{request.ProjectId:N}";
        var approved = store.TryConsumeGrant(
            request.ApprovalGrant ?? string.Empty,
            divisionId,
            request.ProjectId,
            request.Status,
            DateTimeOffset.UtcNow);
        if (!approved)
        {
            return store.StoreReceipt(
                receiptKey,
                new ProjectAiStatusReceipt(
                    request.ProjectId,
                    ProjectAiStatusReceipt.DenyExecution,
                    ProjectAiStatusReceipt.NotExecutedCompletion,
                    ProjectAiStatusReceipt.ApprovalInvalidCode,
                    false,
                    evidenceReference));
        }

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
                    evidenceReference));
        }

        return store.StoreReceipt(
            receiptKey,
            new ProjectAiStatusReceipt(
                request.ProjectId,
                ProjectAiStatusReceipt.ExecutedExecution,
                ProjectAiStatusReceipt.CommittedCompletion,
                ProjectAiStatusReceipt.StatusUpdatedCode,
                false,
                evidenceReference));
    }
}
