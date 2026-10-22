using Microsoft.Extensions.Logging;

namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// Why an agent or a skill that is saved and in effect may still not work as configured. The
/// administration shows them next to the agent or skill; they never refuse a save.
/// </summary>
public static class NhAssistantConfigurationWarnings
{
    /// <summary>
    /// Neither the model profile of the agent or skill nor any of its fallbacks permits its data
    /// classification, so its model calls fail with <c>assistant-classification-not-permitted</c>
    /// until a profile of the chain permits it.
    /// </summary>
    public const string ClassificationNotPermitted = "classification-not-permitted";
}

/// <summary>
/// Checks a data classification against a model profile and its fallbacks, as the profile resolution
/// does: a profile that does not permit it is skipped for its fallbacks.
/// </summary>
internal static class NhAssistantClassificationChecks
{
    /// <summary>
    /// Whether <paramref name="profile"/> or one of its fallbacks, transitively, permits
    /// <paramref name="classification"/>. A fallback that is not registered permits nothing.
    /// </summary>
    public static bool Permits(
        INhAiModelProfileRegistry profiles,
        NhAiModelProfile profile,
        NhAiDataClassification classification)
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
            if (candidate.PermittedDataClassifications.Contains(classification))
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

    /// <summary>
    /// The warnings of an agent or skill that classifies its model calls with
    /// <paramref name="classification"/> on the profile <paramref name="profileName"/>: none without a
    /// classification of its own, for a profile that is not registered (which other checks report) or
    /// when the profile chain permits it.
    /// </summary>
    public static IReadOnlyList<string> WarningsOf(
        INhAiModelProfileRegistry? profiles,
        string? profileName,
        NhAiDataClassification? classification)
    {
        if (classification is not { } value
            || profiles is null
            || string.IsNullOrWhiteSpace(profileName)
            || !profiles.TryGet(profileName, out var profile)
            || Permits(profiles, profile, value))
        {
            return [];
        }

        return [NhAssistantConfigurationWarnings.ClassificationNotPermitted];
    }

    /// <summary>
    /// Logs a warning for every agent and skill in code whose classification neither its profile nor
    /// any of the profile's fallbacks permits, and for an agent's triage profile that does not permit
    /// the agent's classification. Startup goes on: their model calls fail with
    /// <c>assistant-classification-not-permitted</c> until the profiles change.
    /// </summary>
    public static void LogCodeWarnings(
        NhAssistantRegistrationState registration,
        INhAiModelProfileRegistry profiles,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(logger);
        foreach (var (agentId, options) in registration.CodeAgentRuns)
        {
            if (!registration.TryGetAgent(agentId, out var agent))
            {
                continue;
            }

            if (WarningsOf(profiles, agent.ProfileName, options.DataClassification).Count > 0)
            {
                logger.LogWarning(
                    "Agent {AgentId} classifies its model calls as {Classification}, which neither its profile {ProfileName} nor its fallbacks permit; its model calls fail with assistant-classification-not-permitted.",
                    agentId,
                    options.DataClassification,
                    agent.ProfileName);
            }
            if (WarningsOf(profiles, options.TriageProfileName, options.DataClassification).Count > 0)
            {
                logger.LogWarning(
                    "The triage of agent {AgentId} classifies its model call as {Classification}, which neither its profile {ProfileName} nor its fallbacks permit; its triage fails.",
                    agentId,
                    options.DataClassification,
                    options.TriageProfileName);
            }

            foreach (var skill in options.Skills.Values)
            {
                var profileName = skill.ProfileName ?? agent.ProfileName;
                var classification = skill.DataClassification ?? options.DataClassification;
                if (WarningsOf(profiles, profileName, classification).Count > 0)
                {
                    logger.LogWarning(
                        "Skill {SkillId} of agent {AgentId} classifies its model calls as {Classification}, which neither its profile {ProfileName} nor its fallbacks permit; its model calls fail with assistant-classification-not-permitted.",
                        skill.Id,
                        agentId,
                        classification,
                        profileName);
                }
            }
        }
    }
}
