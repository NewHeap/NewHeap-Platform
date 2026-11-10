using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AI.Chat;
using SampleProjectManagement.Core.Services;
using SampleProjectManagement.DAL;

namespace SampleProjectManagement.Api.Services;

/// <summary>
/// SPM-282: an import that removes projects in one statement, as a bulk synchronization with another
/// system does, without the application's own deletion: no event is published and the projects' agent
/// threads are not forgotten. Afterwards it asks the platform which of the removed projects still have
/// proposals waiting for a decision (<see cref="INhAssistantOpenProposals"/>), without reading the
/// assistant's tables, and revalidates those projects
/// (<see cref="INhAssistantProposalRevalidation.RevalidateSubjectAsync"/>). A project that no longer
/// exists closes its proposals as not applicable, so nobody is asked to decide work on it. Without this
/// step the run maintenance closes them at its next look (<c>NhAssistantLimits.GoneSubjectCheckInterval</c>,
/// an hour by default), and any decision of such a proposal closes it at once.
/// </summary>
public sealed class SampleProjectImport(
    SampleProjectManagementDbContext dbContext,
    INhAssistantOpenProposals openProposals,
    INhAssistantProposalRevalidation revalidation)
{
    /// <summary>
    /// Removes the projects with their tasks and members and closes the proposals that waited on them.
    /// Returns how many proposals closed.
    /// </summary>
    public async Task<int> RemoveAsync(IReadOnlyCollection<Guid> projectIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(projectIds);
        await dbContext.Projects
            .Where(project => projectIds.Contains(project.Id))
            .ExecuteDeleteAsync(cancellationToken);

        var closed = 0;
        foreach (var projectId in projectIds.Distinct())
        {
            var subject = ProjectSubjects.For(projectId);
            var open = await openProposals.ListAsync(
                new NhAssistantOpenProposalQuery { Subject = subject, Limit = 1 },
                cancellationToken);
            if (open.Items.Count == 0)
            {
                continue;
            }

            var revalidated = await revalidation.RevalidateSubjectAsync(subject, cancellationToken);
            closed += revalidated.Invalidated.Count;
        }

        return closed;
    }
}
