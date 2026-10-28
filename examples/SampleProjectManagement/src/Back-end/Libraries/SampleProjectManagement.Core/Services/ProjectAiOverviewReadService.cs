using Microsoft.EntityFrameworkCore;
using SampleProjectManagement.DAL;

namespace SampleProjectManagement.Core.Services;

/// <summary>
/// SPM-372: reads one project, its open tasks and its members for the project overview, filtered,
/// ordered and limited in the database, and always within the authorized division.
/// </summary>
public sealed class ProjectAiOverviewReadService(SampleProjectManagementDbContext dbContext) : IProjectAiOverviewReadService
{
    public Task<ProjectAiProjectItem?> GetProjectForAiAsync(
        Guid divisionId,
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        return dbContext.Projects
            .AsNoTracking()
            .Where(project => project.DivisionId == divisionId && project.Id == projectId)
            .Select(project => new ProjectAiProjectItem(
                project.Id,
                project.Key,
                project.Name,
                project.Status,
                project.Deadline,
                project.OwnerUserId))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ProjectAiOpenTaskItem>> ListOpenTasksForAiAsync(
        Guid divisionId,
        Guid projectId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        return await dbContext.ProjectTasks
            .AsNoTracking()
            .Where(task => task.ProjectId == projectId && task.Project.DivisionId == divisionId && !task.IsCompleted)
            .OrderBy(task => task.CreationDateTime)
            .ThenBy(task => task.Id)
            .Take(Math.Clamp(limit, 1, ProjectOverviewAiTools.MaxOpenTasks))
            .Select(task => new ProjectAiOpenTaskItem(task.Id, task.Title))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ProjectAiMemberItem>> ListMembersForAiAsync(
        Guid divisionId,
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        return await dbContext.ProjectMembers
            .AsNoTracking()
            .Where(member => member.ProjectId == projectId && member.Project.DivisionId == divisionId)
            .OrderByDescending(member => member.Role)
            .ThenBy(member => member.UserId)
            .Select(member => new ProjectAiMemberItem(member.UserId, member.User.UserName ?? string.Empty, member.Role))
            .ToListAsync(cancellationToken);
    }
}
