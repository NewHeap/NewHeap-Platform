using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NewHeap.Platform.Common.Models;
using SampleProjectManagement.DAL.Entities;

namespace SampleProjectManagement.Api.Jobs;

public sealed record ProjectPortfolioAnalysisRequest(
    Guid DivisionId,
    int Passes,
    int DelayPerItemMilliseconds,
    bool FailFirstAttempt);

public sealed record ProjectAnalysisChildRequest(
    Guid ProjectId,
    int Passes,
    int DelayPerItemMilliseconds,
    bool FailFirstAttempt);

public sealed record ProjectAnalysisCursor(int NextPass);

public sealed class ProjectPortfolioAnalysisOperation :
    INhBackgroundOperationHandler<ProjectPortfolioAnalysisRequest>
{
    private readonly IRepository<Project> _projects;

    public ProjectPortfolioAnalysisOperation(IRepository<Project> projects)
    {
        _projects = projects;
    }

    public async Task<TaskResult> ExecuteAsync(
        ProjectPortfolioAnalysisRequest request,
        INhBackgroundOperationContext context,
        CancellationToken cancellationToken)
    {
        await context.Progress.DefineAsync(plan => plan
            .Step("discover-projects", 10, "sample-project-management.background-operations.steps.discover-projects")
            .Step("analyze-portfolio", 80, "sample-project-management.background-operations.steps.analyze-portfolio")
            .Step("publish-summary", 10, "sample-project-management.background-operations.steps.publish-summary"),
            cancellationToken);

        List<Guid> projectIds = [];
        var discoveryResult = await context.Progress.RunStepAsync("discover-projects", async (step, token) =>
        {
            projectIds = await _projects.GetAll()
                .AsNoTracking()
                .Where(project => project.DivisionId == request.DivisionId)
                .OrderBy(project => project.Id)
                .Select(project => project.Id)
                .ToListAsync(token);
            await step.ReportAsync(
                1,
                1,
                "sample-project-management.background-operations.messages.projects-discovered",
                new { count = projectIds.Count },
                token);
            return TaskResult.Succeeded();
        }, cancellationToken);
        if (!discoveryResult.Success)
        {
            return discoveryResult;
        }

        var fanOut = await context.FanOut.RunAsync(
            "analyze-portfolio",
            projectIds.Select(projectId => NhBackgroundOperationFanOut.Item(
                projectId.ToString("N"),
                new ProjectAnalysisChildRequest(
                    projectId,
                    request.Passes,
                    request.DelayPerItemMilliseconds,
                    request.FailFirstAttempt))),
            cancellationToken);
        if (!fanOut.Success)
        {
            return fanOut;
        }

        var publishResult = await context.Progress.RunStepAsync("publish-summary", async (step, token) =>
        {
            var idempotentStep = await context.Idempotency.BeginStepAsync("publish-summary", cancellationToken: token);
            if (!idempotentStep.AlreadyCompleted)
            {
                // If this were an external write, pass ExternalIdempotencyKey to
                // that system. The operation checkpoint alone cannot close the
                // crash window around a non-transactional external side effect.
                await context.SetResultAsync(
                    new NhBackgroundOperationResultReference(
                        "project-portfolio-analysis",
                        context.OperationId.ToString("N"),
                        $"/background-operations/{context.OperationId}"),
                    token);
                var completionResult = await idempotentStep.CompleteAsync(token);
                if (!completionResult.Success)
                {
                    return completionResult;
                }
            }

            await step.ReportAsync(1, 1, cancellationToken: token);
            return TaskResult.Succeeded();
        }, cancellationToken);
        if (!publishResult.Success)
        {
            return publishResult;
        }

        await context.Messages.PublishAsync(
            NhBackgroundOperationMessage.Success(
                "sample-project-management.background-operations.messages.analysis-completed",
                new
                {
                    projectCount = projectIds.Count,
                    passes = request.Passes,
                    childCount = fanOut.Data.Total
                }),
            cancellationToken);
        return TaskResult.Succeeded();
    }
}

public sealed class ProjectAnalysisChildOperation :
    INhBackgroundOperationHandler<ProjectAnalysisChildRequest>
{
    public async Task<TaskResult> ExecuteAsync(
        ProjectAnalysisChildRequest request,
        INhBackgroundOperationContext context,
        CancellationToken cancellationToken)
    {
        await context.Progress.DefineAsync(plan => plan.Step(
            "process-passes",
            1,
            "sample-project-management.background-operations.steps.process-project-passes"),
            cancellationToken);

        var processResult = await context.Progress.RunStepAsync("process-passes", async (step, token) =>
        {
            var checkpoint = await context.Checkpoints.GetAsync<ProjectAnalysisCursor>("project-pass-cursor", token);
            var nextPass = Math.Clamp(checkpoint?.Value.NextPass ?? 0, 0, request.Passes);
            await using var batch = await step.BeginBatchAsync(
                request.Passes,
                new NhBackgroundOperationBatchOptions
                {
                    FlushEveryItems = 5,
                    FlushInterval = TimeSpan.FromMilliseconds(500)
                },
                token);

            for (var pass = nextPass; pass < request.Passes; pass++)
            {
                await context.ThrowIfCancellationRequestedAsync(token);
                await batch.ItemStartedAsync(token);
                await Task.Delay(request.DelayPerItemMilliseconds, token);
                if (request.FailFirstAttempt
                    && context.AttemptNumber == 1
                    && pass == Math.Max(1, request.Passes / 2))
                {
                    var itemResult = await batch.ItemFailedAsync("sample-first-attempt-failure", token);
                    if (!itemResult.Success)
                    {
                        return NhBackgroundOperationRetryResult.Retry(
                            "sample-first-attempt-failure",
                            "background-operation.failed");
                    }
                }

                await batch.ItemSucceededAsync(token);
                if ((pass + 1) % 10 == 0 || pass + 1 == request.Passes)
                {
                    var checkpointResult = await context.Checkpoints.SetAsync(
                        "project-pass-cursor",
                        new ProjectAnalysisCursor(pass + 1),
                        cancellationToken: token);
                    if (!checkpointResult.Success)
                    {
                        return checkpointResult;
                    }
                }
            }
            return TaskResult.Succeeded();
        }, cancellationToken);
        if (!processResult.Success)
        {
            return processResult;
        }

        await context.SetResultAsync(
            new NhBackgroundOperationResultReference(
                "project-analysis",
                request.ProjectId.ToString("N")),
            cancellationToken);
        return TaskResult.Succeeded();
    }
}
