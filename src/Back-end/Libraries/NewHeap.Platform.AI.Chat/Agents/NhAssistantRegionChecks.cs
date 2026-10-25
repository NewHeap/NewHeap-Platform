namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// Checks the assistant's execution region (<see cref="NhAssistantBuilder.UseExecutionRegion"/>,
/// <c>local</c> by default) against a model profile and its fallbacks, as the profile resolution does:
/// a profile that does not permit the region is skipped for its fallbacks.
/// </summary>
internal static class NhAssistantRegionChecks
{
    /// <summary>
    /// Whether <paramref name="profile"/> or one of its fallbacks, transitively, permits
    /// <paramref name="region"/>. A fallback that is not registered permits nothing.
    /// </summary>
    public static bool Permits(
        INhAiModelProfileRegistry profiles,
        NhAiModelProfile profile,
        string region)
    {
        return Any(profiles, profile, candidate => candidate.PermittedExecutionRegions.Contains(region));
    }

    /// <summary>
    /// Whether <paramref name="profile"/> or one of its fallbacks permits <paramref name="classification"/>
    /// and <paramref name="region"/> together, which the resolution needs of the one profile it selects.
    /// </summary>
    public static bool PermitsTogether(
        INhAiModelProfileRegistry profiles,
        NhAiModelProfile profile,
        NhAiDataClassification classification,
        string region)
    {
        return Any(
            profiles,
            profile,
            candidate => candidate.PermittedDataClassifications.Contains(classification)
                         && candidate.PermittedExecutionRegions.Contains(region));
    }

    /// <summary>
    /// Whether the registered profile <paramref name="profileName"/> permits the region through its
    /// fallbacks; <see langword="true"/> for a profile that is not registered, which other checks report.
    /// </summary>
    public static bool PermitsByName(
        INhAiModelProfileRegistry? profiles,
        string? profileName,
        string region)
    {
        return profiles is null
            || string.IsNullOrWhiteSpace(profileName)
            || !profiles.TryGet(profileName, out var profile)
            || Permits(profiles, profile, region);
    }

    /// <summary>
    /// Fails host start when a profile the assistant uses in code permits the execution region neither
    /// itself nor through a fallback: the chat profile (<see cref="NhAssistantBuilder.UseChatProfile"/>),
    /// every agent's profile, and the profiles of the skills and the triage of agents in code. Every
    /// model call of such an agent would be refused, so no retry or later deploy of data changes it.
    /// </summary>
    /// <exception cref="InvalidOperationException">A profile in use does not permit the region.</exception>
    public static void Validate(NhAssistantRegistrationState registration, INhAiModelProfileRegistry profiles)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(profiles);
        var region = registration.ExecutionRegion;
        if (registration.ChatProfileName is { } chatProfile)
        {
            Require(profiles, chatProfile, region, "The assistant chat profile");
        }
        foreach (var agent in registration.Agents.OrderBy(agent => agent.Id, StringComparer.Ordinal))
        {
            Require(profiles, agent.ProfileName, region, $"Agent '{agent.Id}'");
        }
        foreach (var (agentId, options) in registration.CodeAgentRuns.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            if (options.TriageProfileName is { } triage)
            {
                Require(profiles, triage, region, $"The triage of agent '{agentId}'");
            }
            foreach (var skill in options.Skills.Values.Where(skill => skill.ProfileName is not null).OrderBy(skill => skill.Id, StringComparer.Ordinal))
            {
                Require(profiles, skill.ProfileName!, region, $"Skill '{skill.Id}' of agent '{agentId}'");
            }
        }
    }

    private static void Require(INhAiModelProfileRegistry profiles, string profileName, string region, string owner)
    {
        if (profiles.TryGet(profileName, out var profile) && !Permits(profiles, profile, region))
        {
            throw new InvalidOperationException(
                $"{owner} uses model profile '{profileName}', which permits the execution region '{region}' neither itself nor through a fallback. "
                + "Permit the region on the profile or a fallback (PermitExecutionRegions), or choose the region with UseExecutionRegion.");
        }
    }

    private static bool Any(
        INhAiModelProfileRegistry profiles,
        NhAiModelProfile profile,
        Func<NhAiModelProfile, bool> permits)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(profile);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var candidates = new Queue<NhAiModelProfile>();
        candidates.Enqueue(profile);
        while (candidates.Count > 0)
        {
            var candidate = candidates.Dequeue();
            if (!visited.Add(candidate.Name))
            {
                continue;
            }
            if (permits(candidate))
            {
                return true;
            }

            foreach (var fallbackName in candidate.FallbackProfileNames)
            {
                if (profiles.TryGet(fallbackName, out var fallback))
                {
                    candidates.Enqueue(fallback);
                }
            }
        }

        return false;
    }
}
