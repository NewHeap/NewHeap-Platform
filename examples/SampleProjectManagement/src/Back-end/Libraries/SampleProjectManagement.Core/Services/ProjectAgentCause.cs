using NewHeap.Platform.AI;

namespace SampleProjectManagement.Core.Services;

/// <summary>
/// SPM-308: the agent whose tool makes a change, and the subject it works in, which the application
/// stamps on the event the change publishes. A trigger event's filter compares them with the agent and
/// subject it decides for, so an agent does not start itself again on the subject it changed, while its
/// change while working on another subject still starts it. The agent run the change belongs to lets
/// the application trace the change back to that run.
/// </summary>
/// <param name="AgentActorId">The agent's actor, <see cref="NhAiInvocationContext.ActorId"/> of the
/// tool call.</param>
/// <param name="SubjectType">The type of the subject the agent works in; <see langword="null"/> when no
/// agent turn is ambient.</param>
/// <param name="SubjectId">The id of that subject.</param>
/// <param name="AgentRunId">The agent run that made the change, <see cref="NhAiInvocationContext.AgentRunId"/>
/// of the tool call; <see langword="null"/> when the agent works outside a run, such as in a person's
/// turn.</param>
public sealed record ProjectAgentCause(string AgentActorId, string? SubjectType, string? SubjectId, Guid? AgentRunId = null)
{
    /// <summary>
    /// The cause of a tool call: the agent, the subject of its ambient invocation scope and its run
    /// when an agent calls the tool, <see langword="null"/> when a person does.
    /// </summary>
    public static ProjectAgentCause? Of(NhAiInvocationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.ActorKind != NhAiActorKind.Agent)
        {
            return null;
        }

        // The run's id is AgentRunId; RunId is the id of the agent's turn.
        var subject = NhAiAgentInvocationScopes.Current?.Subject.Subject;
        return new ProjectAgentCause(context.ActorId, subject?.Type, subject?.Id, context.AgentRunId);
    }
}
