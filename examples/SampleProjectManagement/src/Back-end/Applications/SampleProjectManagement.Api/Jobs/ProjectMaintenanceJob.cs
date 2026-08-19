using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.Common.Utilities;
using SampleProjectManagement.DAL;
using SampleProjectManagement.DAL.Entities;

namespace SampleProjectManagement.Api.Jobs;

public sealed class ProjectMaintenanceJob
{
    private readonly SampleProjectManagementDbContext _dbContext;
    private readonly ILogger<ProjectMaintenanceJob> _logger;

    public ProjectMaintenanceJob(
        SampleProjectManagementDbContext dbContext,
        ILogger<ProjectMaintenanceJob> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task RecalculateOverdueAsync()
    {
        using var projectLock = await StringKeyedSemaphore.LockAsync("project-overdue-recalculation");
        var today = DateTimeOffset.UtcNow.Date;
        var affected = await _dbContext.Projects
            .Where(project => project.Status == ProjectStatus.Active)
            .Where(project => project.Deadline < today)
            .ExecuteUpdateAsync(update => update
                .SetProperty(project => project.Status, ProjectStatus.OnHold)
                .SetProperty(project => project.LastModifiedDateTime, DateTimeOffset.UtcNow));

        _logger.LogInformation(
            "Recalculated {AffectedTaskCount} overdue projects",
            affected);
    }

    public async Task DeleteAbandonedDraftsAsync(DateTimeOffset cutoff)
    {
        using var projectLock = await StringKeyedSemaphore.LockAsync("project-draft-cleanup");
        var affected = await _dbContext.Projects
            .Where(project => project.Status == ProjectStatus.Draft)
            .Where(project => project.LastModifiedDateTime < cutoff)
            .Where(project => !project.Tasks.Any())
            .ExecuteDeleteAsync();

        _logger.LogInformation(
            "Deleted {AffectedProjectCount} abandoned draft projects",
            affected);
    }
}
