using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// The agents registered through <c>AddNewHeapAssistant</c>, their startup validation and the
/// per-user view on them.
/// </summary>
internal sealed class NhAssistantAgentRegistry(NhAssistantRegistrationState state)
{
    public IReadOnlyList<NhAssistantAgentDefinition> Agents => state.Agents
        .OrderBy(agent => agent.Id, StringComparer.Ordinal)
        .ToArray();

    public bool TryGet(string agentId, out NhAssistantAgentDefinition agent)
    {
        agent = state.Agents.FirstOrDefault(
            candidate => string.Equals(candidate.Id, agentId, StringComparison.Ordinal))!;
        return agent is not null;
    }

    /// <summary>
    /// Fails startup when an agent references an unknown or unsuitable profile, an unknown
    /// authorization policy, invalid selectors or an asset whose hash does not match its content.
    /// </summary>
    public async Task ValidateAsync(
        INhAiModelProfileRegistry profiles,
        Func<string, ValueTask<bool>> policyExists,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(policyExists);
        if (state.StorageProvider is null)
        {
            throw new InvalidOperationException(
                "The assistant requires a storage provider. Call UseSqlServer or UsePostgreSql in AddNewHeapAssistant.");
        }
        if (state.Agents.Count == 0)
        {
            throw new InvalidOperationException("The assistant requires at least one agent. Call AddAgent in AddNewHeapAssistant.");
        }
        if (state.ChatProfileName is { } chatProfileName)
        {
            ValidateProfile(profiles, chatProfileName, "The assistant chat profile");
        }

        foreach (var agent in Agents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            NhAssistantAgentDefinition.ValidateShape(agent);
            ValidateProfile(profiles, agent.ProfileName, $"Assistant agent '{agent.Id}'");
            var computedHash = Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes(agent.Instructions.Content)));
            if (!string.Equals(computedHash, agent.Instructions.Manifest.ContentHash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Assistant agent '{agent.Id}' has instructions whose content hash does not match the asset manifest.");
            }
            if (agent.RequiredPolicy is { } policy && !await policyExists(policy))
            {
                throw new InvalidOperationException(
                    $"Assistant agent '{agent.Id}' requires authorization policy '{policy}', which is not registered.");
            }
        }
    }

    /// <summary>
    /// Returns the agents whose <see cref="NhAssistantAgentDefinition.RequiredPolicy"/> the caller satisfies.
    /// </summary>
    public async Task<IReadOnlyList<NhAssistantAgentDefinition>> GetVisibleAsync(
        Func<string, ValueTask<bool>> isAuthorized,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(isAuthorized);
        var visible = new List<NhAssistantAgentDefinition>();
        foreach (var agent in Agents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (agent.RequiredPolicy is null || await isAuthorized(agent.RequiredPolicy))
            {
                visible.Add(agent);
            }
        }
        return visible;
    }

    /// <summary>
    /// Creates the NewHeap agent descriptor for one turn.
    /// </summary>
    public static NhAiAgentDescriptor CreateDescriptor(
        NhAssistantAgentDefinition agent,
        NhAiModelProfile profile,
        IReadOnlyList<string> allowedToolSelectors)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(allowedToolSelectors);
        var baseline = NhAssistantNames.IsSegment(agent.Instructions.Manifest.EvaluationBaselineId)
            ? agent.Instructions.Manifest.EvaluationBaselineId
            : $"{agent.Id}-v{agent.Version.ToString(CultureInfo.InvariantCulture)}";
        return new NhAiAgentDescriptor(
            agent.Id,
            agent.Version,
            agent.Id,
            Bound(agent.DescriptionKey, 512),
            agent.ProfileName,
            NhAiModelCapability.FunctionCalling,
            allowedToolSelectors,
            agent.Autonomy,
            profile.Budget,
            baseline)
        {
            PromptVersion = PromptVersion(agent),
            PromptHash = agent.Instructions.Manifest.ContentHash
        };
    }

    public static string PromptVersion(NhAssistantAgentDefinition agent)
    {
        return $"{agent.Instructions.Manifest.Id}@{agent.Instructions.Manifest.Version.ToString(CultureInfo.InvariantCulture)}";
    }

    private static void ValidateProfile(
        INhAiModelProfileRegistry profiles,
        string profileName,
        string owner)
    {
        if (!profiles.TryGet(profileName, out var profile))
        {
            throw new InvalidOperationException($"{owner} references chat profile '{profileName}', which is not registered.");
        }
        var required = NhAiModelCapability.Chat | NhAiModelCapability.FunctionCalling | NhAiModelCapability.Streaming;
        if ((profile.Capabilities & required) != required)
        {
            throw new InvalidOperationException(
                $"{owner} references chat profile '{profileName}', which must declare chat, function calling and streaming.");
        }
    }

    private static string Bound(string value, int maximum)
    {
        return value.Length <= maximum ? value : value[..maximum];
    }
}
