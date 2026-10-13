using System.ComponentModel;
using Microsoft.AspNetCore.Authorization;
using NewHeap.Platform.AI;
using NewHeap.Platform.Common.Events;
using NewHeap.Platform.Common.Models;
using SampleProjectManagement.Core.Events;

namespace SampleProjectManagement.Core.Services;

public sealed record ProjectAiReviewRequestInput(Guid ProjectId);

public sealed record ProjectAiReviewRequestReceipt(Guid ProjectId, Guid ReviewRequestId);

public sealed record ProjectAiIntakeNoteInput(Guid ProjectId);

public sealed record ProjectAiIntakeNote(Guid ProjectId, Guid ArtifactId, int Version);

/// <summary>
/// The working memory of the intake reviewer about one project (SPM-276). The agent changes it
/// with JSON Merge Patch; every version must fit this shape.
/// </summary>
public sealed record ProjectIntakeMemory(
    IReadOnlyList<string>? Checks,
    string? LastVerdict,
    DateTimeOffset? LastReviewedAt);

/// <summary>
/// Asks the review board to look at a project. Every call sends a new request, so it must not
/// run twice for one decision.
/// </summary>
public interface IProjectReviewRequestService
{
    Task<TaskResult<ProjectAiReviewRequestReceipt>> RequestReviewAsync(
        Guid divisionId,
        Guid projectId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Sends the review request as a <see cref="ProjectReviewRequestedEvent"/> to the review board.
/// </summary>
public sealed class ProjectReviewRequestService(INhEventPublisher eventPublisher) : IProjectReviewRequestService
{
    public async Task<TaskResult<ProjectAiReviewRequestReceipt>> RequestReviewAsync(
        Guid divisionId,
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = new ProjectReviewRequestedEvent
        {
            DivisionId = divisionId,
            ProjectId = projectId
        };
        await eventPublisher.PublishAsync(request);
        return TaskResult<ProjectAiReviewRequestReceipt>.Succeeded(
            new ProjectAiReviewRequestReceipt(projectId, request.EventId));
    }
}

/// <summary>
/// SPM-274: a tool with a side effect that may happen only once. Approved agent proposals for it
/// execute in a background operation that never retries; when the call fails without a result,
/// the proposal waits as <c>execution-unknown</c> until a person resolves it.
/// </summary>
[Authorize]
[NhAiToolSet("projects")]
public sealed class ProjectAiReviewTools(
    IProjectReviewRequestService reviewRequests,
    IProjectAiReadService projects,
    INhAiArtifacts artifacts)
{
    public const string RequestReviewToolId = "projects.request-review";
    public const string DraftIntakeNoteToolId = "projects.draft-intake-note";

    /// <summary>
    /// SPM-276: reads the project and saves an intake note as a versioned artifact of the agent
    /// thread. The note is read-only output of the agent; the next run sees its name and version.
    /// </summary>
    [NhAiTool(
        "draft-intake-note",
        1,
        NhAiToolEffect.ReadOnly,
        NhAiToolExposure.Local | NhAiToolExposure.Agent,
        AllowAgentGrant = true,
        RequiredCapabilities = new[] { ProjectAiTools.ReadCapability })]
    [Authorize(Policy = ProjectAiTools.ViewPolicy)]
    [Description("Draft an intake note for one project in the authorized active division and save it as a file of the agent thread.")]
    public async Task<TaskResult<ProjectAiIntakeNote>> DraftIntakeNoteAsync(
        ProjectAiIntakeNoteInput input,
        NhAiInvocationContext context,
        CancellationToken cancellationToken)
    {
        if (!context.TryGetScopeValue(ProjectAiTools.DivisionScopeKey, out var divisionValue)
            || !Guid.TryParse(divisionValue, out var divisionId))
        {
            return TaskResult<ProjectAiIntakeNote>.Failed(
                ProjectAiTools.DivisionScopeMissingCode,
                "The authorized AI invocation context has no active division.");
        }

        var project = await projects.GetForAiApprovalAsync(divisionId, input.ProjectId, cancellationToken);
        if (project is null)
        {
            return TaskResult<ProjectAiIntakeNote>.Failed("The project is unavailable in the authorized division scope.");
        }

        var note = $"# Intake {project.Key}\n\n- Name: {project.Name}\n- Status: {project.Status}\n";
        var saved = await artifacts.SaveAsync(
            "intake-note-" + project.Key.ToLowerInvariant(),
            "text/markdown",
            System.Text.Encoding.UTF8.GetBytes(note),
            cancellationToken);
        return saved.Success
            ? TaskResult<ProjectAiIntakeNote>.Succeeded(new ProjectAiIntakeNote(project.Id, saved.Data.Id, saved.Data.Version))
            : TaskResult<ProjectAiIntakeNote>.Failed(saved);
    }

    [NhAiTool(
        "request-review",
        1,
        NhAiToolEffect.Mutation,
        NhAiToolExposure.Local | NhAiToolExposure.Agent,
        Approval = NhAiApprovalRequirement.Required,
        Idempotency = NhAiIdempotencySupport.Required,
        AllowAgentGrant = true,
        RequiredCapabilities = new[] { ProjectAiTools.ManageCapability })]
    [Authorize(Policy = ProjectAiTools.ManagePolicy)]
    [Description("Ask the review board to review one project in the authorized active division. Each call sends a new request.")]
    public Task<TaskResult<ProjectAiReviewRequestReceipt>> RequestReviewAsync(
        ProjectAiReviewRequestInput input,
        NhAiInvocationContext context,
        CancellationToken cancellationToken)
    {
        if (!context.TryGetScopeValue(ProjectAiTools.DivisionScopeKey, out var divisionValue)
            || !Guid.TryParse(divisionValue, out var divisionId))
        {
            return Task.FromResult(
                TaskResult<ProjectAiReviewRequestReceipt>.Failed(
                    ProjectAiTools.DivisionScopeMissingCode,
                    "The authorized AI invocation context has no active division."));
        }

        return reviewRequests.RequestReviewAsync(divisionId, input.ProjectId, cancellationToken);
    }
}
