using NewHeap.Platform.AI;

namespace SampleProjectManagement.Core.Services;

/// <summary>
/// SPM-308: the agent whose tool makes a change, and the subject it works in, which the application
/// stamps on the event the change publishes. A trigger event's filter compares them with the agent and
/// subject it decides for, so an agent does not start itself again on the subject it changed, while its
/// change while working on another subject still starts it.
/// </summary>
/// <param name="AgentActorId">The agent's actor, <see cref="NhAiInvocationContext.ActorId"/> of the
/// tool call.</param>
/// <param name="SubjectType">The type of the subject the agent works in; <see langword="null"/> when no
/// agent turn is ambient.</param>
/// <param name="SubjectId">The id of that subject.</param>
public sealed record ProjectAgentCause(string AgentActorId, string? SubjectType, string? SubjectId)
{
    /// <summary>
    /// The cause of a tool call: the agent and the subject of its ambient invocation scope when an
    /// agent calls the tool, <see langword="null"/> when a person does.
    /// </summary>
    public static ProjectAgentCause? Of(NhAiInvocationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.ActorKind != NhAiActorKind.Agent)
        {
            return null;
        }

        var subject = NhAiAgentInvocationScopes.Current?.Subject.Subject;
        return new ProjectAgentCause(context.ActorId, subject?.Type, subject?.Id);
    }
}
