using NewHeap.Platform.AI;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NewHeap.Platform.Common.Models;
using NSubstitute;
using SampleProjectManagement.Api.Jobs;
using SampleProjectManagement.DAL.Entities;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

public sealed class ProjectAiPortfolioReportOperationTests
{
    [Fact]
    public async Task Server_created_proposal_rejects_a_tampered_approval_signal()
    {
        var operationId = Guid.NewGuid();
        var divisionId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(30);
        var snapshot = new ProjectAiPortfolioSnapshot(
            divisionId,
            7,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            NhAiCanonicalJson.ComputeHash("server-owned-snapshot"));
        var operation = Substitute.For<INhBackgroundOperationContext>();
        var checkpoints = Substitute.For<INhBackgroundOperationCheckpointStore>();
        var progress = Substitute.For<INhBackgroundOperationProgressContext>();
        operation.OperationId.Returns(operationId);
        operation.AttemptId.Returns(Guid.NewGuid());
        operation.AttemptNumber.Returns(2);
        operation.FencingToken.Returns(17);
        operation.IdempotencyKey.Returns($"nh-operation-{operationId:N}");
        operation.Checkpoints.Returns(checkpoints);
        operation.Progress.Returns(progress);
        progress
            .ReportAsync(
                Arg.Any<decimal>(),
                Arg.Any<decimal>(),
                Arg.Any<string?>(),
                Arg.Any<object?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        checkpoints
            .GetAsync<ProjectAiPortfolioSnapshot>(
                "ai-report-snapshot",
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<NhBackgroundOperationCheckpointValue<ProjectAiPortfolioSnapshot>?>(
                new NhBackgroundOperationCheckpointValue<ProjectAiPortfolioSnapshot>(
                    snapshot,
                    1,
                    1)));
        checkpoints
            .GetAsync<NhAiProposal>(
                "ai-report-proposal",
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<NhBackgroundOperationCheckpointValue<NhAiProposal>?>(null));
        NhAiProposal? savedProposal = null;
        checkpoints
            .SetAsync(
                "ai-report-proposal",
                Arg.Any<NhAiProposal>(),
                1,
                Arg.Any<long?>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                savedProposal = call.ArgAt<NhAiProposal>(1);
                return Task.FromResult(TaskResult.Succeeded());
            });

        var runAdapter = Substitute.For<INhAiBackgroundOperationRunAdapter>();
        runAdapter
            .BindInvocation(
                Arg.Any<NhAiInvocationContext>(),
                operation,
                Arg.Any<DateTimeOffset?>())
            .Returns(call => call.ArgAt<NhAiInvocationContext>(0) with
            {
                RunId = operationId.ToString("N"),
                RunAttemptNumber = 2,
                IdempotencyKey = operation.IdempotencyKey,
                FencingToken = operation.FencingToken.ToString(),
                Deadline = expiresAt
            });
        runAdapter
            .WaitForApprovalAsync(
                operation,
                "approve-ai-report",
                expiresAt,
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                Assert.NotNull(savedProposal);
                var tamperedHash = (savedProposal.ProposalHash[0] == '0' ? "1" : "0")
                    + savedProposal.ProposalHash[1..];
                return Task.FromResult(
                    new NhBackgroundOperationSignalWaitResult<NhAiBackgroundApprovalSignal>(
                        NhBackgroundOperationSignalWaitStatus.Signaled,
                        new NhAiBackgroundApprovalSignal(
                            Guid.NewGuid(),
                            savedProposal.ProposalId,
                            tamperedHash,
                            true,
                            "approved"),
                        Guid.NewGuid(),
                        DateTimeOffset.UtcNow,
                        expiresAt));
            });
        var projects = Substitute.For<IRepository<Project>>();
        var handler = new ProjectAiPortfolioReportOperation(
            projects,
            runAdapter,
            new NhAiProposalFactory(),
            new NhAiApprovalValidator(new NhAiProposalFactory()));

        var result = await handler.ExecuteAsync(
            new ProjectAiPortfolioReportRequest(divisionId, ownerId, expiresAt),
            operation,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(savedProposal);
        Assert.Equal(operationId, savedProposal.ProposalId);
        Assert.Equal(divisionId, savedProposal.Arguments.GetProperty("divisionId").GetGuid());
        Assert.Equal(snapshot.StateHash, savedProposal.Arguments.GetProperty("snapshotHash").GetString());
        await operation.DidNotReceive().SetResultAsync(
            Arg.Any<NhBackgroundOperationResultReference>(),
            Arg.Any<CancellationToken>());
    }
}
