using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.Chat.Entities;

namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// Checks at startup whether the instructions of an agent leave room for the context it retrieves.
/// </summary>
internal static class NhAssistantRetrievalChecks
{
    /// <summary>
    /// Logs a warning for every agent in code with <c>UseRetrievedContext</c>, and every skill of it,
    /// whose instructions plus retrieval budget exceed
    /// <see cref="NhAssistantPromptComposer.MaxInstructionsLength"/>. Startup goes on: its turns leave
    /// out the passages that do not fit. The check reads what code knows, the agent's instructions and
    /// the seed of the application context. Instructions that change as data, such as an edited
    /// application context or an agent an administrator created, reach the warning of the turn instead.
    /// </summary>
    public static void LogCodeWarnings(NhAssistantRegistrationState registration, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(logger);
        var applicationContext = registration.DefaultApplicationContext is { } seed
            ? new AssistantApplicationContext { Text = seed.Content }
            : null;
        foreach (var (agentId, options) in registration.AgentContexts)
        {
            if (options.RetrievedContext is not { } retrieval || !registration.TryGetAgent(agentId, out var agent))
            {
                continue;
            }

            var budget = Math.Min(retrieval.MaxCharacters, registration.Limits.MaxRetrievedCharacters);
            var instructions = NhAssistantPromptComposer
                .Compose(agent, applicationContext, NhAssistantPreferences.Default, "en")
                .Instructions.Length;
            if (!Fits(instructions, budget))
            {
                logger.LogWarning(
                    "Agent {AgentId} retrieves up to {BudgetCharacters} characters, but its instructions take {InstructionsCharacters} of at most {MaxCharacters}: its turns leave out passages that do not fit. Shorten the instructions or lower MaxCharacters.",
                    agentId,
                    budget,
                    instructions,
                    NhAssistantPromptComposer.MaxInstructionsLength);
                continue;
            }

            if (!registration.CodeAgentRuns.TryGetValue(agentId, out var runOptions))
            {
                continue;
            }

            foreach (var skill in runOptions.Skills.Values.Where(skill => skill.Instructions is not null))
            {
                // A skill's instructions follow the agent's under a heading of its own.
                var withSkill = instructions + "\n\n## Skill: \n".Length + skill.Id.Length + skill.Instructions!.Content.Length;
                if (!Fits(withSkill, budget))
                {
                    logger.LogWarning(
                        "Skill {SkillId} of agent {AgentId} retrieves up to {BudgetCharacters} characters, but its instructions take {InstructionsCharacters} of at most {MaxCharacters}: its turns leave out passages that do not fit. Shorten the instructions or lower MaxCharacters.",
                        skill.Id,
                        agentId,
                        budget,
                        withSkill,
                        NhAssistantPromptComposer.MaxInstructionsLength);
                }
            }
        }
    }

    private static bool Fits(int instructions, int budget)
    {
        return instructions + budget <= NhAssistantPromptComposer.MaxInstructionsLength;
    }
}
