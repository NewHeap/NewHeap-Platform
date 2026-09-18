using SampleProjectManagement.Core.Models.AI;

namespace SampleProjectManagement.Core.Services;

public interface IProjectAiReadService
{
    Task<ProjectAiApprovalItem?> GetForAiApprovalAsync(
        Guid divisionId,
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<ProjectAiApprovalItem?>(null);
    }

    Task<IReadOnlyList<ProjectAiSearchItem>> SearchForAiAsync(
        Guid divisionId,
        string? query,
        int limit,
        CancellationToken cancellationToken = default);
}
