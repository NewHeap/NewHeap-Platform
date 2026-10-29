using System.ComponentModel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AI;
using NewHeap.Platform.Common.Models;
using SampleProjectManagement.DAL;
using SampleProjectManagement.DAL.Entities;

namespace SampleProjectManagement.Core.Services;

/// <param name="DueBy">The last day, in UTC, whose deadlines count.</param>
/// <param name="Before">On <paramref name="DueBy"/>, only deadlines before this time of day in UTC count;
/// the whole day counts when it is absent.</param>
/// <param name="Limit">The most projects to return, at most <see cref="ProjectDeadlineAiTools.MaxProjects"/>.</param>
public sealed record ProjectDueInput(DateOnly DueBy, TimeOnly? Before, int Limit = 10);

/// <summary>An open project and its deadline as a day and a time of day in UTC.</summary>
public sealed record ProjectDueItem(Guid Id, string Key, string Name, DateOnly DueDate, TimeOnly DueTime);

/// <summary>
/// SPM-375: a generated tool with a <see cref="DateOnly"/> and a <see cref="TimeOnly"/> in its input and its
/// output. Its schema describes them as the strings System.Text.Json reads and writes, <c>"2026-11-01"</c>
/// with format <c>date</c> and <c>"14:30:00"</c> with format <c>time</c>, so the model and a script pass a
/// due date as a string, a script sees <c>dueBy: string /* date */</c> and the tool receives a
/// <see cref="DateOnly"/>. The day and the time are in UTC, as the description tells the model.
/// </summary>
[Authorize]
[NhAiToolSet("projects")]
public sealed class ProjectDeadlineAiTools(SampleProjectManagementDbContext dbContext)
{
    public const string DueByToolId = "projects.due-by";

    /// <summary>The function name the model and a script call the tool by.</summary>
    public const string DueByToolName = "projects_due_by_v1";

    /// <summary>The most projects one call returns.</summary>
    public const int MaxProjects = 50;

    [NhAiTool(
        "due-by",
        1,
        NhAiToolEffect.ReadOnly,
        NhAiToolExposure.Local | NhAiToolExposure.Agent,
        RequiredCapabilities = new[] { ProjectAiTools.ReadCapability })]
    [Authorize(Policy = ProjectAiTools.ViewPolicy)]
    [Description("List the open projects of the authorized active division whose deadline falls on or before a day, earliest first. The day and the optional time of day are in UTC; with a time, deadlines on that day count only before it.")]
    public async Task<TaskResult<IReadOnlyList<ProjectDueItem>>> DueByAsync(
        ProjectDueInput input,
        NhAiInvocationContext context,
        CancellationToken cancellationToken)
    {
        if (!context.TryGetScopeValue(ProjectAiTools.DivisionScopeKey, out var divisionValue)
            || !Guid.TryParse(divisionValue, out var divisionId))
        {
            return TaskResult<IReadOnlyList<ProjectDueItem>>.Failed(
                ProjectAiTools.DivisionScopeMissingCode,
                "The authorized AI invocation context has no active division.");
        }

        var dueBefore = DueBefore(input.DueBy, input.Before);
        var projects = await dbContext.Projects
            .AsNoTracking()
            .Where(project => project.DivisionId == divisionId
                && project.Deadline != null
                && project.Deadline < dueBefore
                && project.Status != ProjectStatus.Completed
                && project.Status != ProjectStatus.Archived)
            .OrderBy(project => project.Deadline)
            .ThenBy(project => project.Key)
            .Take(Math.Clamp(input.Limit, 1, MaxProjects))
            .Select(project => new { project.Id, project.Key, project.Name, Deadline = project.Deadline!.Value })
            .ToListAsync(cancellationToken);

        IReadOnlyList<ProjectDueItem> items = projects
            .Select(project =>
            {
                var deadline = project.Deadline.UtcDateTime;
                return new ProjectDueItem(
                    project.Id,
                    project.Key,
                    project.Name,
                    DateOnly.FromDateTime(deadline),
                    TimeOnly.FromDateTime(deadline));
            })
            .ToArray();
        return TaskResult<IReadOnlyList<ProjectDueItem>>.Succeeded(items);
    }

    /// <summary>
    /// The first moment that no longer counts: the time of day on <paramref name="dueBy"/>, or the start of
    /// the next day, in UTC.
    /// </summary>
    private static DateTimeOffset DueBefore(DateOnly dueBy, TimeOnly? before)
    {
        if (before is { } time)
        {
            return new DateTimeOffset(dueBy.ToDateTime(time, DateTimeKind.Utc));
        }

        return dueBy == DateOnly.MaxValue
            ? DateTimeOffset.MaxValue
            : new DateTimeOffset(dueBy.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
    }
}
