using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.Common.Actors;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// The boards of the groups of agents (K15, K16): per group the agents a person sees, each with what
/// it works on, its latest result for the whole application, its open proposals, whether the person
/// may run it now, and whether something is new since the person's last visit of the board in that
/// scope.
/// </summary>
/// <remarks>
/// A board has a scope: the whole application (<c>global</c>), or one division the person may work in.
/// An agent that works on the whole application shows there by its view policy, by default its
/// required policy, as its results do (K14, <see cref="NhAssistantSystemSubjectResolver.MayViewAgentAsync"/>):
/// an app-wide agent on every board of its group, a per-division agent on the board of a division. An
/// agent without work for the whole application in the board's scope shows by its required policy,
/// without a result or a start. Shadow variants and agents without run settings never show; a person
/// bound to a tenant sees no work of the whole application, which belongs to no tenant. One board reads
/// the group's choices, the agents, the threads of at most two subjects, their latest runs, their open
/// proposals and the visit, each in one query.
/// </remarks>
internal sealed class NhAssistantAgentBoards(
    NhAssistantRegistrationState registration,
    NhAssistantAgentGroups groups,
    NhAssistantBoardStore store,
    NhAssistantRunStore runs,
    NhAssistantSubjectAccessService subjects,
    NhAssistantAgentCatalog agents,
    NhAssistantAgentAccess agentAccess,
    NhAssistantManualStarts starts,
    NhAssistantThreadService threadAccess,
    IEnumerable<INhAiSubjectAccessResolver> resolvers,
    IServiceProvider services)
{
    // The visibility of one reader decides with these.
    private readonly NhAssistantAgentAccess _agentAccess = agentAccess;
    private readonly IEnumerable<INhAiSubjectAccessResolver> _resolvers = resolvers;

    /// <summary>
    /// The boards of the groups the reader sees at least one agent of, in the whole application's
    /// scope, ordered by name.
    /// </summary>
    public async Task<NhAssistantBoardListDto> ListAsync(NhAssistantRunReader reader, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var culture = CultureInfo.CurrentUICulture;
        var members = await groups.MembersAsync(null, cancellationToken);
        var visibility = new Visibility(this, reader, BoardScope.Global, await WorkingAgentsAsync(cancellationToken));
        var items = new List<NhAssistantBoardSummaryDto>();
        foreach (var (groupId, agentIds) in members)
        {
            var visible = 0;
            foreach (var agentId in agentIds)
            {
                if (await visibility.SeesAsync(agentId, cancellationToken))
                {
                    visible++;
                }
            }

            if (visible > 0)
            {
                var group = registration.AgentGroups[groupId];
                items.Add(new NhAssistantBoardSummaryDto(groupId, group.NameIn(culture), group.DescriptionIn(culture), visible));
            }
        }

        return new NhAssistantBoardListDto(items
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray());
    }

    /// <summary>
    /// The board of one group in the whole application, or in one division with
    /// <paramref name="divisionId"/>. Fails with <see cref="NhAssistantErrorCodes.BoardNotFound"/> when
    /// the group is not registered, the reader sees none of its agents there or may not work in the
    /// division, and with <see cref="NhAssistantErrorCodes.BoardScopeInvalid"/> for a division id that is
    /// no division id.
    /// </summary>
    public async Task<TaskResult<NhAssistantBoardDto>> GetAsync(
        string groupId,
        string? divisionId,
        NhAssistantRunReader reader,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var board = await VisibleBoardAsync(groupId, divisionId, reader, cancellationToken);
        if (!board.Success)
        {
            return TaskResult<NhAssistantBoardDto>.Failed(board);
        }

        var (scope, visibility, agentIds) = board.Data!;
        var lastVisit = await store.FindVisitAsync(reader.ActorId, groupId, scope.ScopeKey, cancellationToken);

        // The live threads of the agents' subjects in this scope, with their latest runs and open
        // proposals, in one query each.
        var subjectsInScope = agentIds
            .Select(agentId => SubjectOf(visibility.Agents[agentId].RunOptions, scope))
            .OfType<NhAiSubjectRef>()
            .Distinct()
            .ToArray();
        var threads = new List<AssistantThread>();
        foreach (var subject in subjectsInScope)
        {
            threads.AddRange((await runs.FindThreadsBySubjectAsync(subject, cancellationToken))
                .Where(thread => !thread.Shadow && agentIds.Contains(thread.AgentId, StringComparer.Ordinal)));
        }
        var conversationIds = threads.Select(thread => thread.ConversationId).ToArray();
        var latestRuns = (await runs.GetLatestRunsAsync(conversationIds, cancellationToken)).ToDictionary(run => run.ConversationId);
        var openProposals = await runs.GetOpenProposalStatsAsync(conversationIds, cancellationToken);

        var entries = new List<NhAssistantBoardAgentDto>(agentIds.Count);
        foreach (var agentId in agentIds)
        {
            var working = visibility.Agents[agentId];
            var subject = SubjectOf(working.RunOptions, scope);
            var thread = subject is null
                ? null
                : threads.FirstOrDefault(item => string.Equals(item.AgentId, agentId, StringComparison.Ordinal)
                    && item.SubjectType == subject.Type
                    && item.SubjectId == subject.Id);
            var latest = thread is null ? null : latestRuns.GetValueOrDefault(thread.ConversationId);
            var open = 0;
            DateTimeOffset? newestProposal = null;
            if (thread is not null && openProposals.TryGetValue(thread.ConversationId, out var stats))
            {
                (open, newestProposal) = (stats.Count, stats.Newest);
            }

            string? displayName = null;
            var canStart = false;
            if (subject is not null)
            {
                displayName = await DisplayNameAsync(subject, thread, cancellationToken);
                canStart = await CanStartAsync(working.Agent, subject, reader, cancellationToken);
            }
            var readable = thread is not null && await ReadsThreadAsync(thread, reader, cancellationToken);

            entries.Add(new NhAssistantBoardAgentDto(
                agentId,
                working.Agent.Definition.DisplayNameKey,
                working.Agent.Definition.DescriptionKey,
                WorksOnOf(working.RunOptions.WorksOn),
                subject?.Type,
                subject?.Id,
                displayName,
                canStart,
                readable ? thread!.ConversationId : null,
                latest is null ? null : NhAssistantObjectAgents.ToDto(latest),
                open,
                IsNew(latest, newestProposal, lastVisit)));
        }

        var group = registration.AgentGroups[groupId];
        var culture = CultureInfo.CurrentUICulture;
        return TaskResult<NhAssistantBoardDto>.Succeeded(new NhAssistantBoardDto(
            groupId,
            group.NameIn(culture),
            group.DescriptionIn(culture),
            scope.ScopeKey,
            scope.DivisionId?.ToString("D"),
            lastVisit,
            entries));
    }

    /// <summary>
    /// Records that the reader looked at the group's board in the scope now. Fails like
    /// <see cref="GetAsync"/> for a board the reader may not open, so nobody stores visits of boards
    /// they cannot see.
    /// </summary>
    public async Task<TaskResult<NhAssistantBoardVisitDto>> VisitAsync(
        string groupId,
        string? divisionId,
        NhAssistantRunReader reader,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var board = await VisibleBoardAsync(groupId, divisionId, reader, cancellationToken);
        if (!board.Success)
        {
            return TaskResult<NhAssistantBoardVisitDto>.Failed(board);
        }

        var scope = board.Data!.Scope;
        // Whole microseconds, which both providers store exactly.
        var now = DateTimeOffset.UtcNow;
        now = now.AddTicks(-(now.Ticks % 10));
        await store.RecordVisitAsync(reader.ActorId, groupId, scope.ScopeKey, now, cancellationToken);
        return TaskResult<NhAssistantBoardVisitDto>.Succeeded(new NhAssistantBoardVisitDto(groupId, scope.ScopeKey, now));
    }

    /// <summary>
    /// Whether something on a tile is new since the last visit: the latest run ended after it, or an
    /// open proposal was made after it. Before the first visit everything shown is new.
    /// </summary>
    internal static bool IsNew(AssistantRun? latest, DateTimeOffset? newestProposal, DateTimeOffset? lastVisit)
    {
        var ended = latest?.EndedAt;
        if (lastVisit is null)
        {
            return ended is not null || newestProposal is not null;
        }

        return ended > lastVisit || newestProposal > lastVisit;
    }

    /// <summary>
    /// The whole application an agent works on in the board's scope: app-wide the application itself,
    /// per division the board's division; <see langword="null"/> for an agent that does not work on the
    /// application, or a per-division agent on the board of the whole application.
    /// </summary>
    internal static NhAiSubjectRef? SubjectOf(NhAssistantAgentRunOptions runOptions, BoardScope scope)
    {
        var worksOn = runOptions.WorksOn;
        if (!worksOn.Application)
        {
            return null;
        }
        if (!worksOn.PerDivision)
        {
            return NhAiSystemSubjects.Global;
        }

        return scope.DivisionId is { } divisionId ? NhAiSystemSubjects.ForDivision(divisionId) : null;
    }

    private async Task<TaskResult<(BoardScope Scope, Visibility Visibility, IReadOnlyList<string> AgentIds)>> VisibleBoardAsync(
        string groupId,
        string? divisionId,
        NhAssistantRunReader reader,
        CancellationToken cancellationToken)
    {
        if (!registration.AgentGroups.ContainsKey(groupId))
        {
            return TaskResult<(BoardScope, Visibility, IReadOnlyList<string>)>.Failed(NhAssistantErrorCodes.BoardNotFound, "The board was not found.");
        }

        var scope = await ScopeAsync(divisionId, reader, cancellationToken);
        if (!scope.Success)
        {
            return TaskResult<(BoardScope, Visibility, IReadOnlyList<string>)>.Failed(scope);
        }

        var members = (await groups.MembersAsync(groupId, cancellationToken))[groupId];
        var visibility = new Visibility(this, reader, scope.Data!, await WorkingAgentsAsync(cancellationToken));
        var visible = new List<string>(members.Count);
        foreach (var agentId in members)
        {
            if (await visibility.SeesAsync(agentId, cancellationToken))
            {
                visible.Add(agentId);
            }
        }

        return visible.Count == 0
            ? TaskResult<(BoardScope, Visibility, IReadOnlyList<string>)>.Failed(NhAssistantErrorCodes.BoardNotFound, "The board was not found.")
            : TaskResult<(BoardScope, Visibility, IReadOnlyList<string>)>.Succeeded((scope.Data!, visibility, visible));
    }

    /// <summary>
    /// The board's scope: the whole application without a division; a division only when the
    /// application lists it and the reader may work in it.
    /// </summary>
    private async Task<TaskResult<BoardScope>> ScopeAsync(
        string? divisionId,
        NhAssistantRunReader reader,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(divisionId))
        {
            return TaskResult<BoardScope>.Succeeded(BoardScope.Global);
        }
        if (!Guid.TryParseExact(divisionId, "D", out var id) && !Guid.TryParseExact(divisionId, "N", out id))
        {
            return TaskResult<BoardScope>.Failed(NhAssistantErrorCodes.BoardScopeInvalid, "The division id is not a division id.");
        }

        var info = await subjects.DescribeAsync(NhAiSystemSubjects.ForDivision(id), cancellationToken);
        if (info is null
            || reader.TenantId is not null
            || services.GetService<INhDivisionAccess>() is not { } access
            || !await access.HasAccessAsync(new NhActorRef(NhActorKinds.User, reader.ActorId), id, cancellationToken))
        {
            return TaskResult<BoardScope>.Failed(NhAssistantErrorCodes.BoardNotFound, "The board was not found.");
        }

        return TaskResult<BoardScope>.Succeeded(new BoardScope(info.ScopeKey, id));
    }

    /// <summary>
    /// The agents that work now (neither stopped nor disabled in code) with run settings, by id;
    /// shadow variants are left out.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, WorkingAgent>> WorkingAgentsAsync(CancellationToken cancellationToken)
    {
        var working = new Dictionary<string, WorkingAgent>(StringComparer.Ordinal);
        foreach (var agent in await agents.ListAsync(includeDisabled: false, cancellationToken))
        {
            if (registration.AgentRuns.TryGetValue(agent.Id, out var runOptions) && runOptions.ShadowOfAgentId is null)
            {
                working[agent.Id] = new WorkingAgent(agent, runOptions);
            }
        }

        return working;
    }

    private async Task<bool> CanStartAsync(
        NhAssistantAgent agent,
        NhAiSubjectRef subject,
        NhAssistantRunReader reader,
        CancellationToken cancellationToken)
    {
        if (reader.TenantId is not null)
        {
            return false;
        }

        var access = await subjects.ResolveForAgentAsync(subject, agent.Id, reader.ActorId, cancellationToken);
        return await starts.CanStartAsync(agent, subject, access, reader.ActorId, reader.User, cancellationToken);
    }

    /// <summary>
    /// Whether the reader may open the agent's thread: the read role of the whole application and the
    /// agent's view policy, as the thread endpoints decide.
    /// </summary>
    private async Task<bool> ReadsThreadAsync(AssistantThread thread, NhAssistantRunReader reader, CancellationToken cancellationToken)
    {
        if (!string.Equals(thread.TenantId, reader.TenantId, StringComparison.Ordinal))
        {
            return false;
        }

        var access = await threadAccess.ReadAccessAsync(thread, reader.ActorId, cancellationToken);
        return access is not null && access.Role >= NhAiSubjectRole.Reader;
    }

    private async Task<string?> DisplayNameAsync(NhAiSubjectRef subject, AssistantThread? thread, CancellationToken cancellationToken)
    {
        if (NhAiSystemSubjects.IsGlobal(subject))
        {
            return NhAssistantAgentRunDtoMapper.DisplayNameOf(subjects, subject.Type, subject.Id, null);
        }

        return thread?.SubjectDisplayName ?? (await subjects.DescribeAsync(subject, cancellationToken))?.DisplayName;
    }

    private NhAssistantBoardWorksOnDto WorksOnOf(NhAssistantAgentWorksOn worksOn)
    {
        var culture = CultureInfo.CurrentUICulture;
        return new NhAssistantBoardWorksOnDto(
            worksOn.AllSubjects,
            worksOn.Application,
            worksOn.PerDivision,
            worksOn.SubjectTypes
                .Order(StringComparer.Ordinal)
                .Select(type => subjects.FindType(type) is { } known
                    ? new NhAssistantBoardSubjectTypeDto(type, known.NameIn(culture), known.PluralNameIn(culture))
                    : new NhAssistantBoardSubjectTypeDto(type, type, null))
                .ToArray());
    }

    /// <summary>
    /// The scope of a board: the scope key its visits are stored under and its division.
    /// </summary>
    internal sealed record BoardScope(string ScopeKey, Guid? DivisionId)
    {
        public static BoardScope Global { get; } = new(NhAiSubjectInfo.CreateScopeKey(null, null), null);
    }

    private sealed record WorkingAgent(NhAssistantAgent Agent, NhAssistantAgentRunOptions RunOptions);

    /// <summary>
    /// Which agents one reader sees on the boards of one scope, decided once per agent.
    /// </summary>
    private sealed class Visibility(
        NhAssistantAgentBoards boards,
        NhAssistantRunReader reader,
        BoardScope scope,
        IReadOnlyDictionary<string, WorkingAgent> agents)
    {
        private readonly Dictionary<string, bool> _seen = new(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, WorkingAgent> Agents => agents;

        public async Task<bool> SeesAsync(string agentId, CancellationToken cancellationToken)
        {
            if (_seen.TryGetValue(agentId, out var sees))
            {
                return sees;
            }

            sees = await DecideAsync(agentId, cancellationToken);
            _seen[agentId] = sees;
            return sees;
        }

        private async Task<bool> DecideAsync(string agentId, CancellationToken cancellationToken)
        {
            if (!agents.TryGetValue(agentId, out var working))
            {
                return false;
            }

            var subject = SubjectOf(working.RunOptions, scope);
            if (subject is null)
            {
                // No work for the whole application here: the agent shows to the people who may use it.
                return await boards._agentAccess.CanUseAsync(reader.User, working.Agent.Definition);
            }

            // The whole application belongs to no tenant; its results follow the agent's view policy.
            return reader.TenantId is null
                && boards._resolvers.OfType<NhAssistantSystemSubjectResolver>().FirstOrDefault() is { } resolver
                && await resolver.MayViewAgentAsync(subject, agentId, reader.ActorId, cancellationToken);
        }
    }
}
