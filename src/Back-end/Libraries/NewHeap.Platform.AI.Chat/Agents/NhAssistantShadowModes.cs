using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;

namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// Whether an agent or a skill works in shadow now. The code sets where it starts
/// (<c>Shadow()</c>); an administrator's runtime override wins, except for a shadow variant
/// (<c>ShadowOf</c>), which always works in shadow. A skill works in shadow when its agent does or
/// when the skill itself does. Shadow work goes to the agent's shadow thread of the subject.
/// </summary>
internal sealed class NhAssistantShadowModes(
    NhAssistantAgentCatalog agents,
    NhAssistantAdminStore store,
    NhAssistantRegistrationState registration)
{
    /// <summary>
    /// Whether the agent works in shadow, given its runtime override.
    /// </summary>
    public static bool IsAgentShadow(NhAssistantAgentRunOptions options, bool? overridden)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.ShadowOfAgentId is not null || (overridden ?? options.Shadow);
    }

    /// <summary>
    /// Whether the skill itself works in shadow, given its stored state; the agent's mode comes on top.
    /// A chat-only agent has no shadow threads, so its skills always work live, a data skill too (FB8).
    /// </summary>
    public static bool IsSkillShadow(NhAssistantAgentRunOptions agent, NhAssistantSkillOptions skill, AssistantAgentSkillState? row)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(skill);
        return !agent.ChatOnly && (row?.ShadowOverride ?? skill.Shadow);
    }

    /// <summary>
    /// Whether the agent works in shadow now; an agent without a run configuration does not.
    /// </summary>
    public async Task<bool> IsAgentShadowAsync(string agentId, CancellationToken cancellationToken)
    {
        if (!registration.AgentRuns.TryGetValue(agentId, out var options))
        {
            return false;
        }
        if (options.ShadowOfAgentId is not null)
        {
            return true;
        }

        var agent = await agents.FindAsync(agentId, includeDisabled: true, cancellationToken);
        return IsAgentShadow(options, agent?.ShadowOverride);
    }

    /// <summary>
    /// Whether work of the agent in <paramref name="skillId"/>, or without a skill for
    /// <see langword="null"/>, works in shadow now.
    /// </summary>
    public async Task<bool> IsShadowAsync(string agentId, string? skillId, CancellationToken cancellationToken)
    {
        if (await IsAgentShadowAsync(agentId, cancellationToken))
        {
            return true;
        }

        return skillId is not null && (await ShadowSkillsAsync(agentId, cancellationToken)).Contains(skillId);
    }

    /// <summary>
    /// The skills of the agent that work in shadow themselves, whatever the agent's own mode.
    /// </summary>
    public async Task<IReadOnlySet<string>> ShadowSkillsAsync(string agentId, CancellationToken cancellationToken)
    {
        if (!registration.AgentRuns.TryGetValue(agentId, out var options) || options.Skills.Count == 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var stored = await store.GetSkillStatesAsync(agentId, cancellationToken);
        return options.Skills.Values
            .Where(skill => IsSkillShadow(options, skill, stored.GetValueOrDefault(skill.Id)))
            .Select(skill => skill.Id)
            .ToHashSet(StringComparer.Ordinal);
    }
}
