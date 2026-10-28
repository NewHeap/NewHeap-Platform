using System.Security.Claims;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// The one rule of who may run an agent by hand (K9), for every run a person asks for:
/// <c>POST threads/start</c>, a restart of a failed run, a message to a busy agent thread and the
/// generated request tool, as well as a new thread of <c>POST threads/open</c> and every
/// <c>canStart</c> people see. The agent works on the subject (<see cref="NhAssistantWorksOnRule"/>,
/// with its scope for the whole application) and the person may use the agent (its required policy as
/// it is in effect now). On an object the person takes part in it: the participant role from the
/// application's resolver. On the whole application the agent's run policy decides, by default the
/// administration policy, evaluated as the roles of the whole application are; the person reads it as
/// well, so the thread the start leads to can be opened. An agent in shadow works in shadow threads,
/// which administrators alone see, so only they start it. A run a person asks for is created only with
/// the <see cref="NhAssistantStartClearance"/> this rule issues.
/// </summary>
internal sealed class NhAssistantManualStarts(
    NhAssistantRegistrationState registration,
    NhAssistantWorksOnRule worksOn,
    NhAssistantAgentAccess agentAccess,
    IEnumerable<INhAiSubjectAccessResolver> resolvers,
    IOptionsMonitor<NhAssistantOptions> options,
    NhAssistantSubjectAccessService subjects,
    NhAssistantAgentCatalog agents)
{
    /// <summary>
    /// The start rule for a person who asks for a run of <paramref name="agentId"/> on
    /// <paramref name="subject"/>: with the agent as it is in effect now and the person's live role for
    /// the agent's thread there, so the whole application's thread counts only for people who pass the
    /// agent's view policy. Succeeds with the clearance the run's fire needs, and fails as
    /// <see cref="CheckStartAsync"/> does.
    /// </summary>
    public async Task<TaskResult<NhAssistantStartClearance>> ClearAsync(
        string agentId,
        NhAiSubjectRef subject,
        string actorId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(subject);
        var access = await subjects.ResolveForAgentAsync(subject, agentId, actorId, cancellationToken);
        var agent = await agents.FindAsync(agentId, includeDisabled: false, cancellationToken);
        return await ClearAsync(agent, subject, access, actorId, user, cancellationToken);
    }

    /// <summary>
    /// The start rule for a caller that already holds the agent and the person's live role on the
    /// subject; see <see cref="ClearAsync(string, NhAiSubjectRef, string, ClaimsPrincipal, CancellationToken)"/>.
    /// </summary>
    public async Task<TaskResult<NhAssistantStartClearance>> ClearAsync(
        NhAssistantAgent? agent,
        NhAiSubjectRef subject,
        NhAiSubjectAccess access,
        string actorId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var allowed = await CheckStartAsync(agent, subject, access, actorId, user, cancellationToken);
        if (!allowed.Success)
        {
            return TaskResult<NhAssistantStartClearance>.Failed(allowed);
        }

        return TaskResult<NhAssistantStartClearance>.Succeeded(new NhAssistantStartClearance(agent!.Id, subject, actorId));
    }

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
    /// Whether the person may start the agent now, as <c>canStart</c> says: nothing refuses it, the
    /// agent is enabled and agents work on this host.
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
            && NhAssistantAgentWorkGate.IsOn(options)
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

/// <summary>
/// That a person may start an agent on a subject (K9), for the fire that names that agent, subject
/// and person as its initiator. <see cref="NhAssistantAgentTriggers"/> creates the run or the waiting
/// input of a run a person asks for (<see cref="NhAssistantTriggerIds.Manual"/>,
/// <see cref="NhAssistantTriggerIds.QueuedMessage"/>, <see cref="NhAssistantTriggerIds.AgentRequest"/>)
/// only with a clearance that covers it. <see cref="NhAssistantManualStarts"/> issues it once the start
/// rule passed; <see cref="INhAssistantAgentTriggers.StartAsync"/> issues it for the application, whose
/// callers check the start themselves.
/// </summary>
internal sealed record NhAssistantStartClearance(string AgentId, NhAiSubjectRef Subject, string ActorId)
{
    /// <summary>
    /// Whether the clearance is for the agent, subject and initiator of <paramref name="fire"/>.
    /// </summary>
    public bool Covers(NhAssistantTriggerFire fire)
    {
        ArgumentNullException.ThrowIfNull(fire);
        return string.Equals(AgentId, fire.AgentId, StringComparison.Ordinal)
               && Subject == fire.Subject
               && string.Equals(ActorId, fire.InitiatorActorId, StringComparison.Ordinal);
    }
}
