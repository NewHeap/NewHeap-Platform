using NewHeap.Platform.Common.Models;
using SampleProjectManagement.Core.Models.AI;
using SampleProjectManagement.DAL.Entities;

namespace SampleProjectManagement.Core.Services;

public interface IProjectAiMutationService
{
    /// <param name="agentCause">SPM-308: the agent whose tool changes the status, stamped on the
    /// status event; <see langword="null"/> when a person calls the tool.</param>
    Task<TaskResult<ProjectAiStatusChangeReport>> ChangeStatusForAiAsync(
        Guid divisionId,
        Guid projectId,
        ProjectStatus status,
        ProjectAgentCause? agentCause,
        CancellationToken cancellationToken = default);

    Task<ProjectStatus?> GetStatusForAiAsync(
        Guid divisionId,
        Guid projectId,
        CancellationToken cancellationToken = default);
}
