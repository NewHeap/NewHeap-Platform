using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models.View;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NewHeap.Platform.Common.Models;
using SampleProjectManagement.Api.Jobs;
using SampleProjectManagement.Core.Services;

namespace SampleProjectManagement.Api.Composition;

/// <summary>
/// SPM-222: the long-running MCP tool <c>projects.analyze-portfolio</c> and its status tool over the
/// portfolio analysis of the background operations sample (SPM-083). The analysis is the durable work: a
/// background operation of the caller in the active division, found again by the caller's request id.
/// The MCP task of a client with tasks only waits for it; the API's MCP exposure enables tasks with
/// <c>UseTasks</c> in Program.cs.
/// </summary>
public static class SamplePortfolioAnalysisToolsComposition
{
    public static IServiceCollection AddSamplePortfolioAnalysisTools(this IServiceCollection services)
    {
        services.TryAddScoped<IProjectPortfolioAnalyses, SampleProjectPortfolioAnalyses>();
        services.TryAddScoped<ProjectPortfolioAnalysisAiTools>();
        services.AddNewHeapPlatformAI(ai => ai.AddGeneratedToolCatalog<ProjectPortfolioAnalysisAiToolsNhAiCatalog>());
        return services;
    }
}

/// <summary>
/// SPM-222: the portfolio analysis as a background operation of the caller: a light analysis of every
/// project of the division, enqueued once per request id, read back only for its owner and division.
/// </summary>
public sealed class SampleProjectPortfolioAnalyses(
    INhBackgroundOperationService operations,
    TimeProvider? timeProvider = null) : IProjectPortfolioAnalyses
{
    /// <summary>How often a waiting tool reads the analysis again.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    /// <summary>The operation type Program.cs registers the portfolio analysis under.</summary>
    public const string OperationType = "sample-project-portfolio-analysis";

    private const string IdempotencyScope = "ai-portfolio-analysis";

    public async Task<TaskResult<ProjectPortfolioAnalysis>> StartAsync(
        Guid divisionId,
        string actorId,
        string requestId,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(actorId, out var ownerUserId))
        {
            return NotFound();
        }

        var enqueued = await operations.EnqueueAsync(
            new ProjectPortfolioAnalysisRequest(divisionId, Passes: 1, DelayPerItemMilliseconds: 10, FailFirstAttempt: false),
            new NhBackgroundOperationEnqueueOptions
            {
                OwnerUserId = ownerUserId,
                DivisionId = divisionId,
                IdempotencyKey = requestId,
                IdempotencyScope = IdempotencyScope,
                DomainObjectType = "division",
                DomainObjectId = divisionId.ToString("N")
            },
            cancellationToken);
        return enqueued.Success
            ? TaskResult<ProjectPortfolioAnalysis>.Succeeded(ToAnalysis(enqueued.Data!))
            : TaskResult<ProjectPortfolioAnalysis>.Failed(enqueued);
    }

    public async Task<TaskResult<ProjectPortfolioAnalysis>> GetAsync(
        Guid divisionId,
        string actorId,
        Guid analysisId,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(actorId, out var ownerUserId))
        {
            return NotFound();
        }

        var operation = await operations.GetForUserAsync(analysisId, ownerUserId, cancellationToken: cancellationToken);
        if (operation is null
            || operation.DivisionId != divisionId
            || !string.Equals(operation.OperationType, OperationType, StringComparison.Ordinal))
        {
            return NotFound();
        }

        return TaskResult<ProjectPortfolioAnalysis>.Succeeded(ToAnalysis(operation));
    }

    public async Task<TaskResult<ProjectPortfolioAnalysis>> WaitAsync(
        Guid divisionId,
        string actorId,
        Guid analysisId,
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var analysis = await GetAsync(divisionId, actorId, analysisId, cancellationToken);
            if (!analysis.Success || analysis.Data!.Finished)
            {
                return analysis;
            }

            await Task.Delay(PollInterval, timeProvider ?? TimeProvider.System, cancellationToken);
        }
    }

    private static ProjectPortfolioAnalysis ToAnalysis(NhBackgroundOperationViewModel operation)
    {
        var status = operation.Status switch
        {
            NhBackgroundOperationStatus.Succeeded => ProjectPortfolioAnalysisStatuses.Succeeded,
            NhBackgroundOperationStatus.Failed or NhBackgroundOperationStatus.TimedOut => ProjectPortfolioAnalysisStatuses.Failed,
            NhBackgroundOperationStatus.Cancelled => ProjectPortfolioAnalysisStatuses.Cancelled,
            _ => ProjectPortfolioAnalysisStatuses.Running
        };
        return new ProjectPortfolioAnalysis(
            operation.Id,
            status,
            status != ProjectPortfolioAnalysisStatuses.Running);
    }

    private static TaskResult<ProjectPortfolioAnalysis> NotFound()
    {
        return TaskResult<ProjectPortfolioAnalysis>.Failed(
            ProjectPortfolioAnalysisAiTools.AnalysisNotFoundCode,
            "The portfolio analysis does not exist for the caller.");
    }
}
