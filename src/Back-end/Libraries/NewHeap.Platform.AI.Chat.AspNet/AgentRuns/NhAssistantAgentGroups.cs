using System.Globalization;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// The groups of agents (K15): the groups registered in code with <c>AddAgentGroup</c>, and which
/// agents are in each. An agent is in a group when an administrator put it there, or when it joins in
/// code with <c>InGroup</c> and no administrator took it out: the administrator's choice per group
/// wins over code. Agents created in the administration join groups through the administration only.
/// </summary>
public interface INhAssistantAgentGroups
{
    /// <summary>
    /// The groups registered in code, ordered by id, named in the current UI culture.
    /// </summary>
    IReadOnlyList<NhAssistantAgentGroupDto> ListGroups();

    /// <summary>
    /// The groups one agent is in now and those it joins in code. Fails with
    /// <see cref="NhAssistantErrorCodes.AgentNotFound"/> for an unknown agent.
    /// </summary>
    Task<TaskResult<NhAssistantAgentGroupMembershipDto>> GetMembershipAsync(
        string agentId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Puts the agent in exactly <paramref name="groupIds"/> as an administrator: the choice is stored
    /// where it differs from code and wins over code; where it equals code, the code decides again.
    /// Fails with <see cref="NhAssistantErrorCodes.AgentNotFound"/> for an unknown agent, a field error
    /// on <c>groups</c> for an unknown group or too many, <see cref="NhAssistantErrorCodes.ShadowVariant"/>
    /// for a shadow variant, which boards never show, and <see cref="NhAssistantAdminErrorCodes.VersionConflict"/>
    /// when another administrator changed the agent's groups at the same time.
    /// </summary>
    Task<TaskResult<NhAssistantAgentGroupMembershipDto>> SetMembershipAsync(
        string agentId,
        IReadOnlyCollection<string> groupIds,
        string actorId,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="INhAssistantAgentGroups"/>
internal sealed class NhAssistantAgentGroups(
    NhAssistantRegistrationState registration,
    NhAssistantBoardStore store,
    NhAssistantAgentCatalog agents) : INhAssistantAgentGroups
{
    private const int MaxActorIdLength = 256;

    public IReadOnlyList<NhAssistantAgentGroupDto> ListGroups()
    {
        var culture = CultureInfo.CurrentUICulture;
        return registration.AgentGroups.Values
            .OrderBy(group => group.Id, StringComparer.Ordinal)
            .Select(group => new NhAssistantAgentGroupDto(group.Id, group.NameIn(culture), group.DescriptionIn(culture)))
            .ToArray();
    }

    public async Task<TaskResult<NhAssistantAgentGroupMembershipDto>> GetMembershipAsync(
        string agentId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        if (await agents.FindAsync(agentId, includeDisabled: true, cancellationToken) is null)
        {
            return TaskResult<NhAssistantAgentGroupMembershipDto>.Failed(NhAssistantErrorCodes.AgentNotFound, "The agent was not found.");
        }

        var memberships = await MembershipsAsync([agentId], cancellationToken);
        return TaskResult<NhAssistantAgentGroupMembershipDto>.Succeeded(memberships[agentId]);
    }

    public async Task<TaskResult<NhAssistantAgentGroupMembershipDto>> SetMembershipAsync(
        string agentId,
        IReadOnlyCollection<string> groupIds,
        string actorId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(groupIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        if (actorId.Length > MaxActorIdLength)
        {
            throw new ArgumentOutOfRangeException(nameof(actorId), $"An actor id has at most {MaxActorIdLength} characters.");
        }

        var errors = new NhAssistantValidationErrors()
            .Require(groupIds.Count <= NhAssistantAgentGroupDefinition.MaxGroups, "groups", NhAssistantFieldErrors.TooLong)
            .Require(groupIds.All(groupId => groupId is not null && registration.AgentGroups.ContainsKey(groupId)), "groups", NhAssistantFieldErrors.NotFound);
        if (!errors.IsEmpty)
        {
            return TaskResult<NhAssistantAgentGroupMembershipDto>.Failed(errors.ToResult());
        }
        var agent = await agents.FindAsync(agentId, includeDisabled: true, cancellationToken);
        if (agent is null)
        {
            return TaskResult<NhAssistantAgentGroupMembershipDto>.Failed(NhAssistantErrorCodes.AgentNotFound, "The agent was not found.");
        }

        var wanted = groupIds.ToHashSet(StringComparer.Ordinal);
        // A data shadow variant joins no group either, also while its run settings are out of effect.
        if (wanted.Count > 0
            && (agent.ShadowOfAgentId is not null
                || (registration.AgentRuns.TryGetValue(agentId, out var runOptions) && runOptions.ShadowOfAgentId is not null)))
        {
            return TaskResult<NhAssistantAgentGroupMembershipDto>.Failed(
                NhAssistantErrorCodes.ShadowVariant,
                "A shadow variant works for administrators only; boards show live agents.");
        }

        // Only a choice that differs from code is stored; one that equals code lets code decide again.
        var codeGroups = CodeGroupsOf(agentId);
        var choices = new Dictionary<string, bool?>(StringComparer.Ordinal);
        foreach (var groupId in registration.AgentGroups.Keys)
        {
            var member = wanted.Contains(groupId);
            choices[groupId] = member == codeGroups.Contains(groupId, StringComparer.Ordinal) ? null : member;
        }

        var saved = await store.SetMembershipsAsync(agentId, choices, actorId, DateTimeOffset.UtcNow, cancellationToken);
        if (!saved.Success)
        {
            return TaskResult<NhAssistantAgentGroupMembershipDto>.Failed(saved);
        }

        var memberships = await MembershipsAsync([agentId], cancellationToken);
        return TaskResult<NhAssistantAgentGroupMembershipDto>.Succeeded(memberships[agentId]);
    }

    /// <summary>
    /// The groups each of <paramref name="agentIds"/> is in now and joins in code, with one read of the
    /// administrators' choices.
    /// </summary>
    internal async Task<IReadOnlyDictionary<string, NhAssistantAgentGroupMembershipDto>> MembershipsAsync(
        IReadOnlyCollection<string> agentIds,
        CancellationToken cancellationToken)
    {
        var choices = agentIds.Count == 1
            ? await store.ListMembershipsOfAgentAsync(agentIds.First(), cancellationToken)
            : await store.ListMembershipsAsync(null, cancellationToken);
        var byAgent = choices
            .Where(choice => registration.AgentGroups.ContainsKey(choice.GroupId))
            .GroupBy(choice => choice.AgentId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        var memberships = new Dictionary<string, NhAssistantAgentGroupMembershipDto>(StringComparer.Ordinal);
        foreach (var agentId in agentIds.Distinct(StringComparer.Ordinal))
        {
            var codeGroups = CodeGroupsOf(agentId);
            var groups = codeGroups.ToHashSet(StringComparer.Ordinal);
            foreach (var choice in byAgent.GetValueOrDefault(agentId) ?? [])
            {
                if (choice.IsMember)
                {
                    groups.Add(choice.GroupId);
                }
                else
                {
                    groups.Remove(choice.GroupId);
                }
            }

            memberships[agentId] = new NhAssistantAgentGroupMembershipDto(
                agentId,
                groups.Order(StringComparer.Ordinal).ToArray(),
                codeGroups);
        }

        return memberships;
    }

    /// <summary>
    /// The agents in each registered group now, by group id, with one read of the administrators'
    /// choices; <paramref name="groupId"/> narrows it to one group.
    /// </summary>
    internal async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> MembersAsync(
        string? groupId,
        CancellationToken cancellationToken)
    {
        var members = registration.AgentGroups.Keys
            .Where(id => groupId is null || string.Equals(id, groupId, StringComparison.Ordinal))
            .ToDictionary(id => id, _ => new SortedSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var (agentId, runOptions) in registration.CodeAgentRuns)
        {
            foreach (var id in runOptions.Groups.Where(members.ContainsKey))
            {
                members[id].Add(agentId);
            }
        }

        foreach (var choice in await store.ListMembershipsAsync(groupId, cancellationToken))
        {
            if (!members.TryGetValue(choice.GroupId, out var group))
            {
                continue;
            }

            if (choice.IsMember)
            {
                group.Add(choice.AgentId);
            }
            else
            {
                group.Remove(choice.AgentId);
            }
        }

        return members.ToDictionary(
            entry => entry.Key,
            entry => (IReadOnlyList<string>)entry.Value.ToArray(),
            StringComparer.Ordinal);
    }

    private IReadOnlyList<string> CodeGroupsOf(string agentId)
    {
        return registration.CodeAgentRuns.TryGetValue(agentId, out var runOptions) ? runOptions.Groups : [];
    }
}
