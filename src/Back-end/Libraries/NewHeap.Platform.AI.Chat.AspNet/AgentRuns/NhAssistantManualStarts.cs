using System.Security.Claims;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// The one rule of who may run an agent by hand (K9), for <c>POST threads/start</c>, a new thread of
/// <c>POST threads/open</c> and every <c>canStart</c> people see. The agent works on the subject
/// (<see cref="NhAssistantWorksOnRule"/>, with its scope for the whole application) and the person
/// may use the agent (its required policy). On an object the person takes part in it: the
/// participant role from the application's resolver. On the whole application the agent's run policy
/// decides, by default the administration policy, evaluated as the roles of the whole application
/// are; the person reads it as well, so the thread the start leads to can be opened. An agent in
/// shadow works in shadow threads, which administrators alone see, so only they start it.
/// </summary>
internal sealed class NhAssistantManualStarts(
    NhAssistantRegistrationState registration,
    NhAssistantWorksOnRule worksOn,
    NhAssistantAgentAccess agentAccess,
    IEnumerable<INhAiSubjectAccessResolver> resolvers,
    IOptionsMonitor<NhAssistantOptions> options)
{
    /// <summary>
    /// Whether the person may run <paramref name="agent"/> by hand on <paramref name="subject"/>: a
    /// failure with <see cref="NhAssistantErrorCodes.SubjectAccessDenied"/> without the role,
    /// <see cref="NhAssistantErrorCodes.AgentForbidden"/> when the person may not use the agent, run it
    /// for the whole application or start an agent in shadow, and
    /// <see cref="NhAssistantErrorCodes.AgentDoesNotWorkOn"/> when the agent does not work on the
    /// subject. <paramref name="access"/> is the person's live role on the subject. The checks follow
    /// the order the endpoints answered before: the role, the agent, what it works on, then the run
    /// policy and shadow.
    /// </summary>
    public async Task<TaskResult> CheckStartAsync(
        NhAssistantAgent? agent,
        NhAiSubjectRef subject,
        NhAiSubjectAccess access,
        string actorId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(user);
        var system = NhAiSystemSubjects.IsSystem(subject);
        if (!access.Has(system ? NhAiSubjectRole.Reader : NhAiSubjectRole.Participant))
        {
            return Refused(NhAssistantErrorCodes.SubjectAccessDenied, "The person may not start an agent on this subject.");
        }
        if (agent is null
            || !registration.AgentRuns.TryGetValue(agent.Id, out var runOptions)
            || !await agentAccess.CanUseAsync(user, agent.Definition))
        {
            return Refused(NhAssistantErrorCodes.AgentForbidden, "The person may not use this agent.");
        }
        // An agent starts only on what it works on, by hand as from a trigger.
        if (worksOn.RefusalOf(agent.Id, subject) is { } notWorkedOn)
        {
            return Refused(notWorkedOn, "The agent does not work on this subject.");
        }
        if (system && !await MayRunForApplicationAsync(runOptions, subject, actorId, cancellationToken))
        {
            return Refused(NhAssistantErrorCodes.AgentForbidden, "The person may not run this agent for the whole application.");
        }
        if (NhAssistantShadowModes.IsAgentShadow(runOptions, agent.ShadowOverride) && !await agentAccess.CanAdministerAsync(user))
        {
            return Refused(NhAssistantErrorCodes.AgentForbidden, "Only administrators start an agent in shadow.");
        }

        return TaskResult.Succeeded();
    }

    /// <summary>
    /// Whether the person may start the agent now, as <c>canStart</c> says: nothing refuses it and the
    /// agent is enabled.
    /// </summary>
    public async Task<bool> CanStartAsync(
        NhAssistantAgent? agent,
        NhAiSubjectRef subject,
        NhAiSubjectAccess access,
        string actorId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        return agent is not null
            && registration.AgentRuns.TryGetValue(agent.Id, out var runOptions)
            && runOptions.Enabled
            && (await CheckStartAsync(agent, subject, access, actorId, user, cancellationToken)).Success;
    }

    private static TaskResult Refused(string code, string message)
    {
        return TaskResult.Failed(code, message);
    }

    private async Task<bool> MayRunForApplicationAsync(
        NhAssistantAgentRunOptions runOptions,
        NhAiSubjectRef subject,
        string actorId,
        CancellationToken cancellationToken)
    {
        var resolver = resolvers.OfType<NhAssistantSystemSubjectResolver>().FirstOrDefault();
        if (resolver is null)
        {
            return false;
        }

        var policy = NhAssistantSystemSubjectResolver.RunPolicyOf(runOptions, registration, options.CurrentValue);
        return await resolver.MayRunAsync(subject, actorId, policy, cancellationToken);
    }
}
