using SampleProjectManagement.Core.Models.AI;

namespace SampleProjectManagement.Core.Services;

public interface IProjectAiContextService
{
    Task<IReadOnlyList<ProjectAiContextDocument>> SearchContextForAiAsync(
        Guid divisionId,
        string query,
        int limit,
        CancellationToken cancellationToken = default);
}
