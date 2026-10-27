using System.Globalization;
using System.Security.Claims;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// The agents that work on one subject and that a person may use, for an object's page and for the
/// whole application (<c>GET subjects/{type}/{id}/agents</c>): the agents with a run configuration
/// that the one rule of what agents work on lets work on the subject, scope included, whose required
/// policy the person passes, and, for administrators only, shadow variants. Per agent its newest run
/// with the beginning of its result, its open proposals, and whether the person may run it now by
/// the rule of manual starts (K9). The person needs a role on the subject, and the subject must be in
/// the person's tenant (<see cref="NhAssistantWholeApplicationTenancy"/>); work in shadow threads counts
/// for administrators only.
/// </summary>
internal sealed class NhAssistantObjectAgents(
    NhAssistantRegistrationState registration,
    NhAssistantSubjectAccessService subjects,
    NhAssistantRunStore runs,
    NhAssistantAgentCatalog agents,
    NhAssistantAgentAccess agentAccess,
    NhAssistantWorksOnRule worksOn,
    NhAssistantManualStarts starts)
{
    /// <param name="tenantId">The person's tenant, <see langword="null"/> for a person without one: a
    /// subject of another tenant, and the whole application for a person bound to a tenant that takes no
    /// part in it, is unknown.</param>
    public async Task<TaskResult<NhAssistantObjectAgentsDto>> ListAsync(
        NhAiSubjectRef subject,
        string actorId,
        string? tenantId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(user);
        if (!subjects.IsKnownType(subject.Type))
        {
            return TaskResult<NhAssistantObjectAgentsDto>.Failed(NhAssistantErrorCodes.SubjectTypeUnknown, "The subject type is not registered.");
        }

        var info = await subjects.DescribeForAsync(subject, tenantId, cancellationToken);
        var access = info is null ? NhAiSubjectAccess.None : await subjects.ResolveAsync(subject, actorId, cancellationToken);
        if (info is null || access.Role == NhAiSubjectRole.None)
        {
            return TaskResult<NhAssistantObjectAgentsDto>.Failed(NhAssistantErrorCodes.SubjectNotFound, "The subject was not found.");
        }

        var administrator = await agentAccess.CanAdministerAsync(user);
        var candidates = await CandidatesAsync(subject, actorId, user, administrator, cancellationToken);

        // The threads of the subject in its scope and the person's tenant; shadow threads are for
        // administrators only.
        var threads = (await runs.FindThreadsBySubjectAsync(subject, cancellationToken))
            .Where(thread => string.Equals(thread.ScopeKey, info.ScopeKey, StringComparison.Ordinal)
                && NhAssistantWholeApplicationTenancy.Reaches(registration, thread, tenantId)
                && (administrator || !thread.Shadow)
                && candidates.Any(agent => string.Equals(agent.Id, thread.AgentId, StringComparison.Ordinal)))
            .ToArray();
        var conversationIds = threads.Select(thread => thread.ConversationId).ToArray();
        var latestRuns = (await runs.GetLatestRunsAsync(conversationIds, cancellationToken))
            .ToDictionary(run => run.ConversationId);
        var openProposals = await runs.CountOpenThreadApprovalsAsync(conversationIds, cancellationToken);

        var entries = new List<NhAssistantObjectAgentDto>(candidates.Count);
        foreach (var agent in candidates)
        {
            var own = threads.Where(thread => string.Equals(thread.AgentId, agent.Id, StringComparison.Ordinal)).ToArray();
            var variant = registration.AgentRuns[agent.Id].ShadowOfAgentId is not null;
            // People open the live thread; a shadow variant has shadow threads only.
            var opened = own.FirstOrDefault(thread => !thread.Shadow || variant);
            var latest = own
                .Select(thread => latestRuns.GetValueOrDefault(thread.ConversationId))
                .OfType<AssistantRun>()
                .MaxBy(run => run.CreatedAt);
            entries.Add(new NhAssistantObjectAgentDto(
                agent.Id,
                agent.Definition.DisplayNameKey,
                agent.Definition.DescriptionKey,
                await starts.CanStartAsync(agent, subject, access, actorId, user, cancellationToken),
                opened?.ConversationId,
                latest is null ? null : ToDto(latest, CultureInfo.CurrentUICulture),
                own.Sum(thread => openProposals.GetValueOrDefault(thread.ConversationId))));
        }

        var (typeName, link) = NhAssistantAgentRunDtoMapper.SubjectOf(subjects, subject.Type, subject.Id);
        return TaskResult<NhAssistantObjectAgentsDto>.Succeeded(new NhAssistantObjectAgentsDto(
            subject.Type,
            subject.Id,
            NhAssistantAgentRunDtoMapper.DisplayNameOf(subjects, subject.Type, subject.Id, info.DisplayName),
            NhAssistantAgentRunDtoMapper.RoleName(access.Role),
            entries)
        {
            SubjectTypeName = typeName,
            SubjectLink = link
        });
    }

    /// <summary>
    /// The working agents with runs that work on the subject and that the person may use, by id; on the
    /// whole application only those whose thread the person reads, by the agent's view policy.
    /// </summary>
    private async Task<IReadOnlyList<NhAssistantAgent>> CandidatesAsync(
        NhAiSubjectRef subject,
        string actorId,
        ClaimsPrincipal user,
        bool administrator,
        CancellationToken cancellationToken)
    {
        var candidates = new List<NhAssistantAgent>();
        foreach (var agent in await agents.ListAsync(includeDisabled: false, cancellationToken))
        {
            if (!registration.AgentRuns.TryGetValue(agent.Id, out var runOptions)
                || (runOptions.ShadowOfAgentId is not null && !administrator)
                || !worksOn.Allows(agent.Id, subject)
                || !await agentAccess.CanUseAsync(user, agent.Definition)
                || !await subjects.MayReadAgentThreadAsync(subject, agent.Id, actorId, cancellationToken))
            {
                continue;
            }

            candidates.Add(agent);
        }

        return candidates.OrderBy(agent => agent.Id, StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// The latest run with the beginning of its result in <paramref name="culture"/>, the reader's
    /// culture, when its rules wrote it per culture.
    /// </summary>
    internal static NhAssistantLatestRunDto ToDto(AssistantRun run, CultureInfo culture)
    {
        var (result, truncated) = NhAssistantRunResults.PreviewOf(run, culture);
        return new NhAssistantLatestRunDto(
            run.Id,
            run.ConversationId,
            run.TriggerId,
            run.Status,
            run.FailureCode,
            run.EndedAt ?? run.StartedAt ?? run.CreatedAt,
            result,
            truncated,
            run.Shadow);
    }
}
