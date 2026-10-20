using NewHeap.Platform.AI.Chat.Live;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// The work of agents for the whole application that one person may follow live: the approvers of
/// the whole application and the readers of its agents' results, while a screen such as the approval
/// inbox or a board shows it. The whole application lists no members, so its updates cannot reach these
/// people as members of the subject; instead their connection joins the work they may read, which the
/// server decides here with the rule of the agent's thread there: the read role of the whole
/// application and the agent's view policy, by default its required policy (W7 and the review fixes).
/// The live hub repeats the check whenever the client renews its watch, at least every
/// <see cref="RefreshAfter"/>.
/// </summary>
internal sealed class NhAssistantAgentWorkWatchers(
    NhAssistantRunStore runs,
    NhAssistantAgentCatalog agents,
    NhAssistantRegistrationState registration,
    NhAssistantWorksOnRule worksOn,
    NhAssistantSubjectAccessService subjects,
    NhAssistantSystemRoleCache cache)
{
    /// <summary>
    /// The most work of the whole application one person follows: the application itself per agent
    /// and the existing threads in its divisions.
    /// </summary>
    internal const int MaxWatched = 200;

    /// <summary>
    /// How long a watch lasts before the client renews it, so the server checks the person's rights
    /// again.
    /// </summary>
    public static readonly TimeSpan RefreshAfter = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The work of the whole application the person may follow: of every working agent that works on
    /// it app-wide, also before its first run, and of every live thread of an agent there, such as an
    /// agent's work in a division; each only when the agent still works on it and the person reads the
    /// agent's thread there. A person bound to a tenant follows none, as the whole application belongs
    /// to no tenant.
    /// </summary>
    public async Task<IReadOnlyList<NhAssistantWatchedWork>> WatchableAsync(
        string actorId,
        string? tenantId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        if (tenantId is not null)
        {
            return [];
        }

        var candidates = new List<NhAssistantWatchedWork>();
        foreach (var agent in await agents.ListAsync(includeDisabled: false, cancellationToken))
        {
            if (registration.AgentRuns.TryGetValue(agent.Id, out var options)
                && options.ShadowOfAgentId is null
                && worksOn.Allows(agent.Id, NhAiSystemSubjects.Global))
            {
                candidates.Add(new NhAssistantWatchedWork(agent.Id, NhAiSystemSubjects.GlobalId));
            }
        }
        foreach (var thread in await runs.GetLiveSystemThreadsAsync(MaxWatched, cancellationToken))
        {
            if (worksOn.Allows(thread.AgentId, new NhAiSubjectRef(NhAiSubjectRef.SystemType, thread.SubjectId)))
            {
                candidates.Add(new NhAssistantWatchedWork(thread.AgentId, thread.SubjectId));
            }
        }

        var watchable = new List<NhAssistantWatchedWork>();
        foreach (var work in candidates.Distinct().Take(MaxWatched))
        {
            if (await MayReadAsync(work, actorId, cancellationToken))
            {
                watchable.Add(work);
            }
        }

        return watchable;
    }

    private async Task<bool> MayReadAsync(NhAssistantWatchedWork work, string actorId, CancellationToken cancellationToken)
    {
        if (cache.TryGetView(work.SubjectId, work.AgentId, actorId, out var known))
        {
            return known;
        }

        var may = await subjects.MayReadAgentThreadAsync(
            new NhAiSubjectRef(NhAiSubjectRef.SystemType, work.SubjectId),
            work.AgentId,
            actorId,
            cancellationToken);
        cache.SetView(work.SubjectId, work.AgentId, actorId, may);
        return may;
    }
}
