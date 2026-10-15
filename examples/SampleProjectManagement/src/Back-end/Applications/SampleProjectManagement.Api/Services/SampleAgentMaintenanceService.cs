using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.Common.Models;
using SampleProjectManagement.Api.Composition;

namespace SampleProjectManagement.Api.Services;

/// <summary>
/// SPM-284: the application's own maintenance of its agents, through
/// <see cref="INhAssistantAgentControl"/>. Before a large task import an operator pauses the
/// project closer, so the task events of the import wait instead of starting runs, and resumes it
/// afterwards: each project then gets one run that sees all its changes. When the closer proposes
/// wrongly, the operator stops it: nothing new starts and approved proposals wait until the closer
/// is resumed. A state change never changes an agent's definition or version.
/// </summary>
public sealed class SampleAgentMaintenanceService(INhAssistantAgentControl agents)
{
    /// <summary>
    /// The agents that work on their own and that the maintenance page offers.
    /// </summary>
    public static readonly IReadOnlyList<string> AgentIds =
    [
        SampleIntakeReviewerComposition.AgentId,
        SampleStatusAdvisorComposition.AgentId,
        SampleProjectCloserComposition.AgentId,
        SampleProjectPlannerComposition.AgentId
    ];

    public async Task<IReadOnlyList<NhAssistantAgentState>> ListAsync(CancellationToken cancellationToken)
    {
        var states = new List<NhAssistantAgentState>(AgentIds.Count);
        foreach (var agentId in AgentIds)
        {
            if (await agents.GetAsync(agentId, cancellationToken) is { } state)
            {
                states.Add(state);
            }
        }

        return states;
    }

    /// <summary>
    /// Pauses, stops or resumes one of the sample's agents for the operator. Returns
    /// <see langword="null"/> for an agent the page does not offer.
    /// </summary>
    public async Task<TaskResult<NhAssistantAgentState>?> ChangeAsync(
        string agentId,
        SampleAgentStateChange change,
        Guid operatorId,
        string? reason,
        CancellationToken cancellationToken)
    {
        if (!AgentIds.Contains(agentId, StringComparer.Ordinal))
        {
            return null;
        }

        var actorId = operatorId.ToString("D");
        return change switch
        {
            SampleAgentStateChange.Pause => await agents.PauseAsync(agentId, actorId, reason, cancellationToken),
            SampleAgentStateChange.Stop => await agents.StopAsync(agentId, actorId, reason, cancellationToken),
            _ => await agents.ResumeAsync(agentId, actorId, reason, cancellationToken)
        };
    }

    /// <summary>
    /// SPM-286: pauses, stops or resumes one skill of one of the sample's agents; its other skills
    /// keep working. Returns <see langword="null"/> for an agent the page does not offer.
    /// </summary>
    public async Task<TaskResult<NhAssistantSkillState>?> ChangeSkillAsync(
        string agentId,
        string skillId,
        SampleAgentStateChange change,
        Guid operatorId,
        string? reason,
        CancellationToken cancellationToken)
    {
        if (!AgentIds.Contains(agentId, StringComparer.Ordinal))
        {
            return null;
        }

        var actorId = operatorId.ToString("D");
        return change switch
        {
            SampleAgentStateChange.Pause => await agents.PauseSkillAsync(agentId, skillId, actorId, reason, cancellationToken),
            SampleAgentStateChange.Stop => await agents.StopSkillAsync(agentId, skillId, actorId, reason, cancellationToken),
            _ => await agents.ResumeSkillAsync(agentId, skillId, actorId, reason, cancellationToken)
        };
    }
}

public enum SampleAgentStateChange
{
    Pause,
    Stop,
    Resume
}
