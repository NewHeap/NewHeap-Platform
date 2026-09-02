using NewHeap.Platform.Common.Models;
using SampleProjectManagement.Core.Models.AI;
using SampleProjectManagement.DAL.Entities;

namespace SampleProjectManagement.Core.Services;

public interface IProjectAiMutationService
{
    Task<TaskResult<ProjectAiStatusChangeReport>> ChangeStatusForAiAsync(
        Guid divisionId,
        Guid projectId,
        ProjectStatus status,
        CancellationToken cancellationToken = default);

    Task<ProjectStatus?> GetStatusForAiAsync(
        Guid divisionId,
        Guid projectId,
        CancellationToken cancellationToken = default);
}
