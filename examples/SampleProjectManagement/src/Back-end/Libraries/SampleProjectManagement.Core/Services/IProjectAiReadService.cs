using SampleProjectManagement.Core.Models.AI;

namespace SampleProjectManagement.Core.Services;

public interface IProjectAiReadService
{
    Task<IReadOnlyList<ProjectAiSearchItem>> SearchForAiAsync(
        Guid divisionId,
        string? query,
        int limit,
        CancellationToken cancellationToken = default);
}
