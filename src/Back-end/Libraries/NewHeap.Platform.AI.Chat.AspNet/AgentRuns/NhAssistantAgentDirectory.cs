using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// Reads agents as they are in effect, for the application's own pages, reports and processes: the
/// definition the assistant uses (the code, or the administrator's override, or the data of an agent
/// created at runtime), its instructions resolved from an asset, its run state and shadow mode and its
/// metadata. It is a server API: it authorizes nothing and returns the instructions in full, so the
/// application decides who may see what it shows. People's chat contract stays
/// <c>GET agents</c>, the administration's <c>GET admin/agents</c>.
/// </summary>
public interface INhAssistantAgentDirectory
{
    /// <summary>
    /// The agent as it is in effect, or <see langword="null"/> for an agent that does not exist.
    /// Stopped, paused, switched-off and code-disabled agents are found too.
    /// </summary>
    Task<NhAssistantAgentInEffectDto?> GetAsync(string agentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every agent as it is in effect, ordered by id, whatever its state, narrowed by
    /// <paramref name="filter"/>.
    /// </summary>
    Task<IReadOnlyList<NhAssistantAgentInEffectDto>> ListAsync(
        NhAssistantAgentDirectoryFilter? filter = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Narrows <see cref="INhAssistantAgentDirectory.ListAsync"/>.
/// </summary>
public sealed record NhAssistantAgentDirectoryFilter
{
    /// <summary>
    /// Only agents of this source: <c>code</c> or <c>admin</c> (<see cref="NhAssistantAgentSources"/>);
    /// <see langword="null"/> for both.
    /// </summary>
    public string? Source { get; init; }

    /// <summary>
    /// Whether shadow variants, in code (<c>ShadowOf</c>) and created as data, are listed;
    /// <see langword="true"/> by default.
    /// </summary>
    public bool IncludeShadowVariants { get; init; } = true;
}

/// <summary>
/// An agent as it is in effect.
/// </summary>
/// <param name="AgentId">The agent's id.</param>
/// <param name="Source"><c>code</c> for an agent registered in code, <c>admin</c> for one created at
/// runtime.</param>
/// <param name="Version">The version of its definition, the one
/// <see cref="INhAssistantAgentDefinitions.UpdateAsync"/> expects.</param>
/// <param name="DisplayName">The name in effect: for an agent in code a key of the application's
/// translations, unless an administrator overrode it.</param>
/// <param name="Description">The description in effect, a key for an agent in code likewise.</param>
/// <param name="Instructions">The instructions in effect: the text, or the verified text of the asset
/// they come from; <see langword="null"/> when the asset is not available
/// (<see cref="InstructionsProblem"/>).</param>
/// <param name="InstructionsHash">The SHA-256 of the instructions in effect, or of the asset as the agent
/// names it.</param>
/// <param name="ToolSelectors">The tools the agent may use.</param>
/// <param name="Autonomy">How far the agent may act on its own.</param>
/// <param name="Profile">The model profile of its model calls: from its code for an agent in code.</param>
/// <param name="RunState"><c>active</c>, <c>paused</c>, <c>stopped</c>, <c>off</c> or
/// <c>configured-off</c>, as <see cref="INhAssistantAgentControl"/> reports it.</param>
public sealed record NhAssistantAgentInEffectDto(
    string AgentId,
    string Source,
    int Version,
    string DisplayName,
    string Description,
    string? Instructions,
    string InstructionsHash,
    IReadOnlyList<string> ToolSelectors,
    NhAiAutonomyLevel Autonomy,
    string Profile,
    string RunState)
{
    /// <summary>
    /// Whether an administrator overrides the definition of an agent in code.
    /// </summary>
    public bool IsOverridden { get; init; }

    /// <summary>
    /// The policy a person must pass to use the agent; <see langword="null"/> for none.
    /// </summary>
    public string? RequiredPolicy { get; init; }

    /// <summary>
    /// The MCP servers assigned to the agent, ordered.
    /// </summary>
    public IReadOnlyList<string> McpServerIds { get; init; } = [];

    /// <summary>
    /// The asset of the application an agent created at runtime takes its instructions from;
    /// <see langword="null"/> when they are text.
    /// </summary>
    public NhAssistantInstructionAssetReference? InstructionsAsset { get; init; }

    /// <summary>
    /// Why the instructions of <see cref="InstructionsAsset"/> are not available, one of the asset
    /// problems of <see cref="NhAssistantRunConfigurationProblems"/>, such as <c>asset-missing</c>: the
    /// agent then starts no model turn. <see langword="null"/> when they resolve.
    /// </summary>
    public string? InstructionsProblem { get; init; }

    /// <summary>
    /// The classification of the data its model calls carry; <see langword="null"/> for
    /// <see cref="NhAiDataClassification.Internal"/>.
    /// </summary>
    public NhAiDataClassification? DataClassification { get; init; }

    /// <summary>
    /// Whether people can choose the agent in the chat.
    /// </summary>
    public bool OfferedInChat { get; init; } = true;

    /// <summary>
    /// The live agent this agent is a shadow variant of, in code or created as data;
    /// <see langword="null"/> for an agent that is no variant.
    /// </summary>
    public string? ShadowOf { get; init; }

    /// <summary>
    /// Whether the agent's runs work in its shadow threads now; <see langword="false"/> for an agent
    /// without a run configuration, which only chats.
    /// </summary>
    public bool InShadow { get; init; }

    /// <summary>
    /// Whether the agent works in people's chats only (<c>ChatOnly()</c> in code): it works on no
    /// subject and is never started on its own.
    /// </summary>
    public bool ChatOnly { get; init; }

    /// <summary>
    /// The agent's state as <see cref="INhAssistantAgentControl.GetAsync"/> returns it: who changed it
    /// when and why, its skills, its shadow mode and technical account.
    /// </summary>
    public required NhAssistantAgentState State { get; init; }

    /// <summary>
    /// The agent metadata in effect, ordered by entry name, as
    /// <see cref="INhAssistantAgentMetadata.GetAgentAsync"/> returns it.
    /// </summary>
    public IReadOnlyList<NhAssistantAgentMetadataValue> Metadata { get; init; } = [];

    /// <summary>
    /// When the definition last changed.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; init; }
}

internal sealed class NhAssistantAgentDirectory(
    NhAssistantAgentCatalog catalog,
    NhAssistantAgentControl control,
    NhAssistantAgentMetadataService metadata,
    NhAssistantAgentMetadataStore metadataStore) : INhAssistantAgentDirectory
{
    public async Task<NhAssistantAgentInEffectDto?> GetAsync(string agentId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        var agent = await catalog.FindAsync(agentId, includeDisabled: true, cancellationToken);
        if (agent is null)
        {
            return null;
        }

        return await ToDtoAsync(agent, await metadataStore.ListAsync(agentId, cancellationToken), cancellationToken);
    }

    public async Task<IReadOnlyList<NhAssistantAgentInEffectDto>> ListAsync(
        NhAssistantAgentDirectoryFilter? filter = null,
        CancellationToken cancellationToken = default)
    {
        filter ??= new NhAssistantAgentDirectoryFilter();
        var agents = (await catalog.ListAsync(includeDisabled: true, cancellationToken))
            .Where(agent => filter.Source is null || string.Equals(agent.Source, filter.Source, StringComparison.Ordinal))
            .OrderBy(agent => agent.Id, StringComparer.Ordinal)
            .ToArray();

        // The metadata of every agent in one read.
        var rows = (await metadataStore.ListAsync(agents.Select(agent => agent.Id).ToArray(), cancellationToken))
            .GroupBy(row => row.AgentId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<AssistantAgentMetadata>)group.ToArray(), StringComparer.Ordinal);
        var list = new List<NhAssistantAgentInEffectDto>(agents.Length);
        foreach (var agent in agents)
        {
            var dto = await ToDtoAsync(agent, rows.GetValueOrDefault(agent.Id) ?? [], cancellationToken);
            if (filter.IncludeShadowVariants || dto.ShadowOf is null)
            {
                list.Add(dto);
            }
        }

        return list;
    }

    private async Task<NhAssistantAgentInEffectDto> ToDtoAsync(
        NhAssistantAgent agent,
        IReadOnlyList<AssistantAgentMetadata> rows,
        CancellationToken cancellationToken)
    {
        var definition = agent.Definition;
        var state = await control.StateOfAsync(agent, cancellationToken);
        return new NhAssistantAgentInEffectDto(
            agent.Id,
            agent.Source,
            definition.Version,
            definition.DisplayNameKey,
            definition.DescriptionKey,
            // The placeholder of instructions that are not available never leaves the assistant.
            agent.InstructionsProblem is null ? definition.Instructions.Content : null,
            agent.InstructionsAsset?.Hash ?? definition.Instructions.Manifest.ContentHash,
            definition.ToolSelectors,
            definition.Autonomy,
            definition.ProfileName,
            state.RunState)
        {
            IsOverridden = agent.IsOverridden,
            RequiredPolicy = definition.RequiredPolicy,
            McpServerIds = agent.McpServerIds,
            InstructionsAsset = agent.InstructionsAsset,
            InstructionsProblem = agent.InstructionsProblem,
            DataClassification = agent.DataClassification,
            OfferedInChat = agent.OfferedInChat,
            // A data shadow variant names its live agent also while its run settings are out of effect.
            ShadowOf = state.ShadowOf ?? agent.ShadowOfAgentId,
            InShadow = state.Shadow?.Shadow == true,
            ChatOnly = agent.ChatOnly,
            State = state,
            Metadata = metadata.ToView(agent, rows).Metadata,
            UpdatedAt = agent.UpdatedAt
        };
    }
}
