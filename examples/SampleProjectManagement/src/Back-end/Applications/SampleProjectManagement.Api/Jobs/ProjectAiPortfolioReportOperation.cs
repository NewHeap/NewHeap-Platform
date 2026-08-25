using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AI;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NewHeap.Platform.Common.Models;
using SampleProjectManagement.Core.Services;
using SampleProjectManagement.DAL.Entities;

namespace SampleProjectManagement.Api.Jobs;

public sealed record ProjectAiPortfolioReportRequest(
    Guid DivisionId,
    Guid AccountableOwnerId,
    DateTimeOffset ApprovalExpiresAt);

public sealed record ProjectAiPortfolioReportArguments(
    Guid DivisionId,
    int ProjectCount,
    string SnapshotHash);

public sealed record ProjectAiPortfolioSnapshot(
    Guid DivisionId,
    int ProjectCount,
    DateTimeOffset CapturedAt,
    string StateHash);

public sealed class ProjectAiPortfolioReportOperation(
    IRepository<Project> projects,
    INhAiBackgroundOperationRunAdapter runAdapter,
    INhAiProposalFactory proposalFactory,
    INhAiApprovalValidator approvalValidator) :
    INhBackgroundOperationHandler<ProjectAiPortfolioReportRequest>
{
    private static readonly NhAiToolDescriptor ReportTool = new(
        "projects.generate-portfolio-report",
        1,
        "Generate and publish the approved project portfolio report.",
        typeof(ProjectAiPortfolioReportArguments),
        typeof(NhBackgroundOperationResultReference),
        NhAiToolEffect.IdempotentMutation,
        NhAiToolExposure.Agent,
        true,
        ["app.active-division.project.manage"])
    {
        Approval = NhAiApprovalRequirement.Required,
        Idempotency = NhAiIdempotencySupport.Required,
        ContractHash = NhAiCanonicalJson.ComputeHash(
            "projects.generate-portfolio-report@1")
    };

    public async Task<TaskResult> ExecuteAsync(
        ProjectAiPortfolioReportRequest request,
        INhBackgroundOperationContext context,
        CancellationToken cancellationToken)
    {
        var asset = ProjectAiAssets.ProjectAgentInstructions.Manifest;
        var invocation = runAdapter.BindInvocation(
            new NhAiInvocationContext(
                "project-report-agent",
                "portfolio-report",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [ProjectAiTools.DivisionScopeKey] = request.DivisionId.ToString()
                })
            {
                ActorKind = NhAiActorKind.Agent,
                AccountableOwnerId = request.AccountableOwnerId.ToString(),
                ExecutionScopes =
                [
                    new NhAiExecutionScopeEntry("division", request.DivisionId.ToString())
                ],
                CapabilityGrants = new HashSet<string>(StringComparer.Ordinal)
                {
                    ProjectAiTools.ReadCapability
                },
                ModelProfileName = "project-assistant",
                PromptVersion = asset.Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
                PromptHash = asset.ContentHash,
                AgentVersion = "1",
                CatalogVersion = "1",
                ContextPolicyId = asset.ContextPolicyId
            },
            context,
            request.ApprovalExpiresAt);

        var snapshotCheckpoint = await context.Checkpoints
            .GetAsync<ProjectAiPortfolioSnapshot>("ai-report-snapshot", cancellationToken);
        var snapshot = snapshotCheckpoint?.Value;
        if (snapshot is null)
        {
            var projectCount = await projects.GetAll()
                .AsNoTracking()
                .CountAsync(project => project.DivisionId == request.DivisionId, cancellationToken);
            var capturedAt = DateTimeOffset.UtcNow;
            var stateHash = NhAiCanonicalJson.ComputeHash(new
            {
                request.DivisionId,
                projectCount,
                capturedAt
            });
            snapshot = new ProjectAiPortfolioSnapshot(
                request.DivisionId,
                projectCount,
                capturedAt,
                stateHash);
            var snapshotResult = await context.Checkpoints.SetAsync(
                "ai-report-snapshot",
                snapshot,
                cancellationToken: cancellationToken);
            if (!snapshotResult.Success)
            {
                return snapshotResult;
            }

            var workflowCheckpoint = NhAiRunCheckpointReferenceFactory.Create(
                "background-operation",
                "project-portfolio-report",
                1,
                "context-captured",
                1,
                stateHash,
                capturedAt);
            var checkpointResult = await runAdapter.SetCheckpointAsync(
                context,
                workflowCheckpoint,
                cancellationToken: cancellationToken);
            if (!checkpointResult.Success)
            {
                return checkpointResult;
            }
        }

        invocation = invocation with { ContextHash = snapshot.StateHash };
        var arguments = new ProjectAiPortfolioReportArguments(
            request.DivisionId,
            snapshot.ProjectCount,
            snapshot.StateHash);
        var proposalCheckpoint = await context.Checkpoints
            .GetAsync<NhAiProposal>("ai-report-proposal", cancellationToken);
        var proposal = proposalCheckpoint?.Value;
        if (proposal is null)
        {
            proposal = proposalFactory.Create(
                new NhAiProposalCreateRequest(
                    context.OperationId,
                    invocation.RunId!,
                    invocation.ActorKind,
                    invocation.ActorId,
                    invocation.AccountableOwnerId!,
                    ReportTool,
                    arguments,
                    [new NhAiProposalTarget("division", request.DivisionId.ToString("N"))],
                    "Generate and publish the current division portfolio report.",
                    ["publish-project-portfolio-report"],
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["snapshot-hash"] = snapshot.StateHash
                    },
                    new NhAiActionBudget(1),
                    DateTimeOffset.UtcNow,
                    request.ApprovalExpiresAt)
                {
                    ModelProfileName = invocation.ModelProfileName,
                    PromptVersion = invocation.PromptVersion,
                    PromptHash = invocation.PromptHash,
                    ContextHash = invocation.ContextHash
                });
            var proposalResult = await context.Checkpoints.SetAsync(
                "ai-report-proposal",
                proposal,
                cancellationToken: cancellationToken);
            if (!proposalResult.Success)
            {
                return proposalResult;
            }
        }

        await context.Progress.ReportAsync(
            1,
            3,
            "sample-project-management.ai-report.awaiting-approval",
            new
            {
                snapshot.ProjectCount,
                runId = invocation.RunId,
                proposalId = proposal.ProposalId,
                proposalHash = proposal.ProposalHash
            },
            cancellationToken);
        var approval = await runAdapter.WaitForApprovalAsync(
            context,
            "approve-ai-report",
            request.ApprovalExpiresAt,
            cancellationToken);
        if (approval.Status == NhBackgroundOperationSignalWaitStatus.Expired)
        {
            return TaskResult.Failed(
                "approval-expired",
                "sample-project-management.ai-report.approval-expired");
        }
        if (approval.Signal is not { Approved: true } signal
            || signal.ProposalId != proposal.ProposalId
            || !string.Equals(signal.ProposalHash, proposal.ProposalHash, StringComparison.Ordinal)
            || approval.SignaledByUserId is null
            || approval.SignaledAt is null)
        {
            return TaskResult.Failed(
                "approval-rejected",
                "sample-project-management.ai-report.approval-rejected");
        }
        var approvalEvidence = new NhAiApprovalEvidence(
            proposal,
            new NhAiApproval(
                signal.ApprovalId,
                proposal.ProposalId,
                proposal.ProposalHash,
                approval.SignaledByUserId.Value.ToString(),
                proposal.Targets,
                proposal.Constraints,
                approval.SignaledAt.Value,
                proposal.ExpiresAt,
                proposal.EstimatedBudget));
        var approvedInvocation = invocation with
        {
            ProposalId = proposal.ProposalId.ToString(),
            ApprovalId = signal.ApprovalId.ToString()
        };
        var validation = approvalValidator.Validate(
            ReportTool,
            approvedInvocation,
            arguments,
            approvalEvidence,
            DateTimeOffset.UtcNow);
        if (!validation.Succeeded)
        {
            return TaskResult.Failed(
                "approval-invalid",
                "sample-project-management.ai-report.approval-invalid");
        }

        await context.Progress.ReportAsync(
            2,
            3,
            "sample-project-management.ai-report.generating",
            cancellationToken: cancellationToken);
        var publish = await context.Idempotency.BeginStepAsync(
            "publish-ai-report",
            cancellationToken: cancellationToken);
        if (!publish.AlreadyCompleted)
        {
            await context.SetResultAsync(
                new NhBackgroundOperationResultReference(
                    "project-ai-portfolio-report",
                    context.OperationId.ToString("N"),
                    $"/reports/project-ai/{context.OperationId:N}"),
                cancellationToken);
            var completion = await publish.CompleteAsync(cancellationToken);
            if (!completion.Success)
            {
                return completion;
            }
        }

        await context.Progress.ReportAsync(
            3,
            3,
            "sample-project-management.ai-report.completed",
            new
            {
                snapshot.ProjectCount,
                snapshot.StateHash,
                approvalId = signal.ApprovalId,
                attempt = invocation.RunAttemptNumber
            },
            cancellationToken);
        return TaskResult.Succeeded();
    }
}
