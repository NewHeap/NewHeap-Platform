using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AI.Chat;
using SampleProjectManagement.Core.Services;
using SampleProjectManagement.DAL;
using SampleProjectManagement.DAL.Entities;

namespace SampleProjectManagement.Api.Composition;

/// <summary>
/// SPM-285: the briefing every model turn of an agent about a project reads after its instructions:
/// the project's status and deadline, its tasks, and what the project closer's check finds. The
/// model starts from these facts instead of reading tasks through tools; the facts carry their
/// dates, and judging them is left to the model. Headless runs read it without a person, so it
/// states only what every project member may see.
/// </summary>
public sealed class ProjectBriefingProvider(SampleProjectManagementDbContext dbContext)
    : INhAssistantSubjectBriefingProvider
{
    private const int MaxListedTasks = 10;

    public string SubjectType => ProjectSubjects.Type;

    public async ValueTask<NhAssistantSubjectBriefing?> GetBriefingAsync(
        NhAssistantBriefingContext context,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(context.Subject.Subject.Id, out var projectId))
        {
            return null;
        }

        var project = await dbContext.Projects
            .AsNoTracking()
            .Where(item => item.Id == projectId)
            .Select(item => new { item.Key, item.Name, item.Status, item.Deadline, item.LastModifiedDateTime })
            .SingleOrDefaultAsync(cancellationToken);
        if (project is null)
        {
            return null;
        }

        var tasks = await dbContext.ProjectTasks
            .AsNoTracking()
            .Where(task => task.ProjectId == projectId)
            .Select(task => new { task.Title, task.IsCompleted, task.CompletedAt })
            .ToListAsync(cancellationToken);
        var today = DateTimeOffset.UtcNow;
        var open = tasks.Where(task => !task.IsCompleted).OrderBy(task => task.Title, StringComparer.Ordinal).ToArray();
        var completed = tasks.Where(task => task.IsCompleted).OrderByDescending(task => task.CompletedAt).ToArray();

        var summary = new StringBuilder()
            .Append(project.Key).Append(' ').Append(project.Name).Append(". Status ")
            .Append(project.Status.ToString().ToLowerInvariant())
            .Append(", last changed on ").Append(Date(project.LastModifiedDateTime)).Append('.');
        summary.AppendLine();
        summary.Append(project.Deadline is { } deadline
            ? $"Deadline {Date(deadline)}, {(deadline - today).Days.ToString(CultureInfo.InvariantCulture)} days from today ({Date(today)})."
            : "No deadline.");

        var list = new StringBuilder()
            .Append(open.Length.ToString(CultureInfo.InvariantCulture)).Append(" of ")
            .Append(tasks.Count.ToString(CultureInfo.InvariantCulture)).Append(" tasks are open.");
        foreach (var task in open.Take(MaxListedTasks))
        {
            list.AppendLine().Append("- open: ").Append(task.Title);
        }
        foreach (var task in completed.Take(MaxListedTasks - Math.Min(open.Length, MaxListedTasks)))
        {
            list.AppendLine().Append("- completed on ").Append(task.CompletedAt is { } at ? Date(at) : "an unknown date")
                .Append(": ").Append(task.Title);
        }

        var check = ProjectCompletionCheck.Evaluate(project.Status, open.Length, tasks.Count);
        return new NhAssistantSubjectBriefing(
        [
            new NhAssistantBriefingSection("Project", summary.ToString()),
            new NhAssistantBriefingSection("Tasks", list.ToString()),
            // Rules signal, the model decides: the closer's own check, reported as a fact.
            new NhAssistantBriefingSection("Completion check of the project closer", check.ReadyToComplete
                ? "Every task is completed and the project is active: the project closer's rules propose to complete it."
                : check.Reason)
        ]);
    }

    private static string Date(DateTimeOffset value)
    {
        return value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// SPM-283 and SPM-285: the domain check that the project closer's rules decide on and the project
/// briefing reports, so rules and the model read the same facts.
/// </summary>
public static class ProjectCompletionCheck
{
    public static async Task<ProjectCompletion?> ReadAsync(
        SampleProjectManagementDbContext dbContext,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var status = await dbContext.Projects
            .AsNoTracking()
            .Where(project => project.Id == projectId)
            .Select(project => (ProjectStatus?)project.Status)
            .SingleOrDefaultAsync(cancellationToken);
        if (status is not { } current)
        {
            return null;
        }

        var tasks = await dbContext.ProjectTasks
            .AsNoTracking()
            .Where(task => task.ProjectId == projectId)
            .Select(task => task.IsCompleted)
            .ToListAsync(cancellationToken);
        return Evaluate(current, tasks.Count(completed => !completed), tasks.Count);
    }

    public static ProjectCompletion Evaluate(ProjectStatus status, int open, int total)
    {
        return new ProjectCompletion(status, open, total);
    }
}

/// <summary>
/// Whether an active project has all its tasks completed.
/// </summary>
public sealed record ProjectCompletion(ProjectStatus Status, int Open, int Total)
{
    public bool IsActive => Status == ProjectStatus.Active;

    public bool ReadyToComplete => IsActive && Total > 0 && Open == 0;

    /// <summary>
    /// Why the project is not ready, in English for the model.
    /// </summary>
    public string Reason => !IsActive
        ? "The project is not active, so the project closer leaves it alone."
        : Total == 0
            ? "The project has no tasks yet."
            : $"{Open.ToString(CultureInfo.InvariantCulture)} of {Total.ToString(CultureInfo.InvariantCulture)} tasks are still open.";
}
