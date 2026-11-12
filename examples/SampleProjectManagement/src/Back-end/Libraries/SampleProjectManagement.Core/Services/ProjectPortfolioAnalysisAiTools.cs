using System.ComponentModel;
using Microsoft.AspNetCore.Authorization;
using NewHeap.Platform.AI;
using NewHeap.Platform.Common.Models;

namespace SampleProjectManagement.Core.Services;

/// <param name="RequestId">The caller's id of this request: the same id finds the same analysis again,
/// so a client that lost the answer retries without starting a second one.</param>
public sealed record ProjectPortfolioAnalysisInput(string RequestId);

public sealed record ProjectPortfolioAnalysisStatusInput(Guid AnalysisId);

/// <summary>
/// One analysis of the active division's portfolio as the caller sees it.
/// </summary>
/// <param name="AnalysisId">The analysis, to poll with <c>projects.portfolio-analysis-status</c>.</param>
/// <param name="Status">One of the values in <see cref="ProjectPortfolioAnalysisStatuses"/>.</param>
/// <param name="Finished">Whether the analysis ended; a client stops polling then.</param>
public sealed record ProjectPortfolioAnalysis(Guid AnalysisId, string Status, bool Finished);

public static class ProjectPortfolioAnalysisStatuses
{
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

/// <summary>
/// The portfolio analysis of the background operations sample (SPM-083) as the AI tools reach it: started
/// for the caller with their request id, read back, and awaited.
/// </summary>
public interface IProjectPortfolioAnalyses
{
    /// <summary>
    /// Starts the analysis of <paramref name="divisionId"/> for the person <paramref name="actorId"/>, or
    /// finds the one this request id started before.
    /// </summary>
    Task<TaskResult<ProjectPortfolioAnalysis>> StartAsync(
        Guid divisionId,
        string actorId,
        string requestId,
        CancellationToken cancellationToken = default);

    /// <summary>The analysis as it is now; fails with <see cref="ProjectPortfolioAnalysisAiTools.AnalysisNotFoundCode"/>
    /// for an analysis of someone else or another division.</summary>
    Task<TaskResult<ProjectPortfolioAnalysis>> GetAsync(
        Guid divisionId,
        string actorId,
        Guid analysisId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The analysis once it ended, polled on the application's clock until then or until
    /// <paramref name="cancellationToken"/>, which carries the tool's timeout.
    /// </summary>
    Task<TaskResult<ProjectPortfolioAnalysis>> WaitAsync(
        Guid divisionId,
        string actorId,
        Guid analysisId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// SPM-222: a long-running tool over MCP. <c>projects.analyze-portfolio</c> declares
/// <see cref="NhAiToolTaskSupport.Optional"/>, so where the API's MCP exposure enables tasks
/// (<c>UseTasks</c>) a client with the MCP Tasks extension gets a task at once and polls
/// <c>tasks/get</c> for the finished analysis, while the tool waits for it within its own timeout. A
/// client without tasks gets the tool's own answer as before: the analysis it started under its request
/// id, which it polls with <c>projects.portfolio-analysis-status</c> until it finished. The tool tells the
/// two apart by <see cref="NhAiInvocationContext.McpTaskId"/>; both run with the caller's rights in the
/// authorized division. A task runs on after its request ended and without the caller's token, so the
/// tools work with the caller of their <see cref="NhAiInvocationContext"/> only: a tool set that takes
/// <c>INhAiCallerCredentialAccessor</c> or <c>IHttpContextAccessor</c> cannot declare task support
/// (<c>NHAI021</c>), and the API bridge's tools, which pass the caller's token on, stay plain calls.
/// </summary>
[Authorize]
[NhAiToolSet("projects")]
public sealed class ProjectPortfolioAnalysisAiTools(IProjectPortfolioAnalyses analyses)
{
    public const string AnalyzeToolId = "projects.analyze-portfolio";
    public const string StatusToolId = "projects.portfolio-analysis-status";

    /// <summary>The analysis does not exist for the caller.</summary>
    public const string AnalysisNotFoundCode = "portfolio-analysis-not-found";

    /// <summary>The request id is missing or longer than <see cref="MaxRequestIdLength"/>.</summary>
    public const string RequestIdInvalidCode = "portfolio-analysis-request-id-invalid";

    public const int MaxRequestIdLength = 100;

    [NhAiTool(
        "analyze-portfolio",
        1,
        NhAiToolEffect.ReadOnly,
        NhAiToolExposure.Local | NhAiToolExposure.Mcp,
        TaskSupport = NhAiToolTaskSupport.Optional,
        // A task holds its concurrency slot while the tool waits, so allow several analyses at once.
        TimeoutSeconds = 600,
        MaxConcurrency = 4,
        RequiredCapabilities = new[] { ProjectAiTools.ReadCapability })]
    [Authorize(Policy = ProjectAiTools.ViewPolicy)]
    [Description("Analyse the portfolio of the authorized active division. Pass your own request id and keep it on a retry. As an MCP task the call returns the finished analysis; otherwise poll projects.portfolio-analysis-status with the returned analysis id until finished.")]
    public async Task<TaskResult<ProjectPortfolioAnalysis>> AnalyzeAsync(
        ProjectPortfolioAnalysisInput input,
        NhAiInvocationContext context,
        CancellationToken cancellationToken)
    {
        if (!TryGetDivision(context, out var divisionId))
        {
            return DivisionMissing();
        }
        if (string.IsNullOrWhiteSpace(input.RequestId) || input.RequestId.Length > MaxRequestIdLength)
        {
            return TaskResult<ProjectPortfolioAnalysis>.Failed(
                RequestIdInvalidCode,
                "Pass a request id of at most 100 characters.");
        }

        var started = await analyses.StartAsync(divisionId, context.ActorId, input.RequestId, cancellationToken);
        if (!started.Success || context.McpTaskId is null || started.Data!.Finished)
        {
            // Without a task the caller polls the status tool itself.
            return started;
        }

        // The MCP client already holds the task and polls it; the tool waits for the analysis.
        return await analyses.WaitAsync(divisionId, context.ActorId, started.Data.AnalysisId, cancellationToken);
    }

    [NhAiTool(
        "portfolio-analysis-status",
        1,
        NhAiToolEffect.ReadOnly,
        NhAiToolExposure.Local | NhAiToolExposure.Mcp,
        RequiredCapabilities = new[] { ProjectAiTools.ReadCapability })]
    [Authorize(Policy = ProjectAiTools.ViewPolicy)]
    [Description("Read a portfolio analysis that projects.analyze-portfolio started, until it is finished.")]
    public async Task<TaskResult<ProjectPortfolioAnalysis>> StatusAsync(
        ProjectPortfolioAnalysisStatusInput input,
        NhAiInvocationContext context,
        CancellationToken cancellationToken)
    {
        if (!TryGetDivision(context, out var divisionId))
        {
            return DivisionMissing();
        }

        return await analyses.GetAsync(divisionId, context.ActorId, input.AnalysisId, cancellationToken);
    }

    private static bool TryGetDivision(NhAiInvocationContext context, out Guid divisionId)
    {
        divisionId = Guid.Empty;
        return context.TryGetScopeValue(ProjectAiTools.DivisionScopeKey, out var divisionValue)
            && Guid.TryParse(divisionValue, out divisionId);
    }

    private static TaskResult<ProjectPortfolioAnalysis> DivisionMissing()
    {
        return TaskResult<ProjectPortfolioAnalysis>.Failed(
            ProjectAiTools.DivisionScopeMissingCode,
            "The authorized AI invocation context has no active division.");
    }
}
