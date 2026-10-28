using System.ComponentModel;
using Microsoft.AspNetCore.Authorization;
using NewHeap.Platform.AI;
using NewHeap.Platform.Common.Models;

namespace SampleProjectManagement.Core.Services;

public sealed record ProjectNextMilestoneInput(Guid ProjectId);

/// <param name="Milestone">The planning service's answer, at most
/// <see cref="ProjectPlanningMilestoneTools.MaxMilestoneLength"/> characters.</param>
public sealed record ProjectNextMilestone(Guid ProjectId, string Milestone);

/// <summary>
/// SPM-362: the remote planning service as the application calls it: one reviewed tool of its MCP
/// server, with the application's own address and credential. The tool that wraps it never sees the
/// MCP client.
/// </summary>
public interface IProjectPlanningMilestones
{
    /// <summary>
    /// The next milestone of the project with key <paramref name="projectKey"/>, as the planning
    /// service answers; fails with <see cref="ProjectPlanningMilestoneTools.NotConfiguredCode"/> while
    /// the application has no planning service, and with
    /// <see cref="ProjectPlanningMilestoneTools.UnavailableCode"/> when it does not answer.
    /// </summary>
    Task<TaskResult<string>> NextMilestoneAsync(string projectKey, CancellationToken cancellationToken = default);
}

/// <summary>
/// SPM-362: the code-owned wrapper of a tool that a remote MCP server offers. An MCP tool the
/// assistant imported is never exported again (FB31): a partner that may call this API's MCP endpoint
/// must not reach the planning service through it on the API's credential. When the application wants
/// to offer what that tool knows, it writes this <c>[NhAiTool]</c> itself and so reviews it like any of
/// its own tools: its own id, contract, effect, policy and capability, its own exposure (MCP included),
/// a project of the authorized division only, one fixed remote tool with fixed arguments and a bounded
/// answer. Every call goes through the governed invoker as any generated tool does.
/// </summary>
[Authorize]
[NhAiToolSet("projects")]
public sealed class ProjectPlanningMilestoneTools(
    IProjectAiReadService projects,
    IProjectPlanningMilestones planning)
{
    public const string NextMilestoneToolId = "projects.next-milestone";

    /// <summary>The application has no planning service configured.</summary>
    public const string NotConfiguredCode = "planning-not-configured";

    /// <summary>The planning service did not answer or answered with an error.</summary>
    public const string UnavailableCode = "planning-unavailable";

    /// <summary>The most characters of the planning service's answer the tool passes on.</summary>
    public const int MaxMilestoneLength = 500;

    [NhAiTool(
        "next-milestone",
        1,
        NhAiToolEffect.ReadOnly,
        NhAiToolExposure.Local | NhAiToolExposure.Agent | NhAiToolExposure.Mcp,
        RequiredCapabilities = new[] { ProjectAiTools.ReadCapability })]
    [Authorize(Policy = ProjectAiTools.ViewPolicy)]
    [Description("Return the next milestone of one project in the authorized active division, as the planning service plans it.")]
    public async Task<TaskResult<ProjectNextMilestone>> NextMilestoneAsync(
        ProjectNextMilestoneInput input,
        NhAiInvocationContext context,
        CancellationToken cancellationToken)
    {
        if (!context.TryGetScopeValue(ProjectAiTools.DivisionScopeKey, out var divisionValue)
            || !Guid.TryParse(divisionValue, out var divisionId))
        {
            return TaskResult<ProjectNextMilestone>.Failed(
                ProjectAiTools.DivisionScopeMissingCode,
                "The authorized AI invocation context has no active division.");
        }

        // The policy authorized the division; the planning service hears only about a project of it,
        // by its key, never anything the caller wrote.
        var project = await projects.GetForAiApprovalAsync(divisionId, input.ProjectId, cancellationToken);
        if (project is null)
        {
            return TaskResult<ProjectNextMilestone>.Failed(
                ProjectAiTools.ProjectOutOfScopeCode,
                "The project is unavailable in the authorized division scope.");
        }

        var milestone = await planning.NextMilestoneAsync(project.Key, cancellationToken);
        if (!milestone.Success)
        {
            return TaskResult<ProjectNextMilestone>.Failed(milestone);
        }

        var text = milestone.Data!.Trim();
        return TaskResult<ProjectNextMilestone>.Succeeded(new ProjectNextMilestone(
            project.Id,
            text.Length <= MaxMilestoneLength ? text : text[..MaxMilestoneLength]));
    }
}
