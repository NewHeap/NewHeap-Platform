using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// How an agent, or one of its skills, uses the characters of a model call.
/// </summary>
/// <param name="AgentId">The agent.</param>
/// <param name="SkillId">The skill with instructions of its own; <see langword="null"/> for the agent itself.</param>
/// <param name="InstructionCharacters">The composed instructions: the library's rules, the application
/// context as stored now, the agent's and skill's instructions and the longest style wishes a person may
/// save, in the language that makes them longest.</param>
/// <param name="RetrievalCharacters">What the agent may retrieve per turn; 0 without retrieved context.</param>
/// <param name="MaxCharacters">The limit of the model call,
/// <see cref="NhAssistantLimits.MaxInstructionCharacters"/>.</param>
public sealed record NhAssistantInstructionBudget(
    string AgentId,
    string? SkillId,
    int InstructionCharacters,
    int RetrievalCharacters,
    int MaxCharacters)
{
    /// <summary>
    /// The characters left once the instructions and the retrieved context are counted. The application
    /// map and the turn data of a turn come out of this too; negative when the turn cannot hold both.
    /// </summary>
    public int RemainingCharacters => MaxCharacters - InstructionCharacters - RetrievalCharacters;

    /// <summary>
    /// Whether a full retrieved context fits next to the longest instructions this agent can compose.
    /// </summary>
    public bool Fits => RemainingCharacters >= 0;
}

/// <summary>
/// Measures what an agent's turns need against the limit of a model call, with the data of this
/// environment, so an application can assert its budget in a test or a health check.
/// </summary>
public interface INhAssistantInstructionBudgets
{
    /// <summary>
    /// The budget of the agent itself, first, and of each of its skills that adds instructions. The
    /// agent may be disabled. Fails with <c>assistant-agent-not-found</c> for an unknown agent and with
    /// <c>assistant-agent-instructions-unavailable</c> when its instructions cannot be read.
    /// </summary>
    Task<TaskResult<IReadOnlyList<NhAssistantInstructionBudget>>> GetAsync(
        string agentId,
        CancellationToken cancellationToken);
}

internal sealed class NhAssistantInstructionBudgets(
    NhAssistantRegistrationState registration,
    NhAssistantAgentCatalog agents,
    NhAssistantPersonalization personalization,
    ILogger<NhAssistantInstructionBudgets> logger) : INhAssistantInstructionBudgets
{
    /// <summary>
    /// The preferences that compose the longest text: the longest style, address form and response
    /// length, with style wishes as long as a person may save them.
    /// </summary>
    private static readonly NhAssistantPreferences LongestPreferences = new(
        NhAssistantStyles.Direct,
        NhAssistantAddressForms.Informal,
        NhAssistantResponseLengths.Long,
        new string('x', NhAssistantPreferences.MaxCustomInstructionsLength));

    private static readonly string[] Languages = ["en", "nl"];

    private bool _applicationContextRead;
    private AssistantApplicationContext? _applicationContext;

    public async Task<TaskResult<IReadOnlyList<NhAssistantInstructionBudget>>> GetAsync(
        string agentId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        var agent = await agents.FindAsync(agentId, includeDisabled: true, cancellationToken);
        if (agent is null)
        {
            return TaskResult<IReadOnlyList<NhAssistantInstructionBudget>>.Failed(
                NhAssistantErrorCodes.AgentNotFound,
                "The assistant agent was not found.");
        }
        if (agent.InstructionsProblem is not null)
        {
            return TaskResult<IReadOnlyList<NhAssistantInstructionBudget>>.Failed(
                NhAssistantErrorCodes.AgentInstructionsUnavailable,
                "The assistant agent's instructions are not available.");
        }

        return TaskResult<IReadOnlyList<NhAssistantInstructionBudget>>.Succeeded(
            Measure(agent, await StoredApplicationContextAsync(cancellationToken)));
    }

    /// <summary>
    /// The warning of an agent whose instructions, or a skill's, do not fit with the data stored now.
    /// </summary>
    public async Task<IReadOnlyList<string>> WarningsOfAsync(NhAssistantAgent agent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);
        if (agent.InstructionsProblem is not null)
        {
            return [];
        }

        var applicationContext = await StoredApplicationContextAsync(cancellationToken);
        return Measure(agent, applicationContext).All(budget => budget.Fits)
            ? []
            : [NhAssistantConfigurationWarnings.InstructionsOverBudget];
    }

    /// <summary>
    /// Warns about every agent and skill that no longer fits now <paramref name="saved"/> is the
    /// application context. Only counts and ids reach the log, never the text.
    /// </summary>
    public async Task LogOverBudgetAsync(AssistantApplicationContext saved, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(saved);
        foreach (var agent in await agents.ListAsync(includeDisabled: true, cancellationToken))
        {
            if (agent.InstructionsProblem is not null)
            {
                continue;
            }

            foreach (var budget in Measure(agent, saved).Where(budget => !budget.Fits))
            {
                logger.LogWarning(
                    "With the saved application context of {ContextCharacters} characters, agent {AgentId} (skill {SkillId}) needs {InstructionsCharacters} characters of instructions and up to {RetrievalCharacters} retrieved, of at most {MaxCharacters}: its turns leave out passages or fail. Shorten the text, lower MaxCharacters or raise MaxInstructionCharacters.",
                    saved.Text.Length,
                    budget.AgentId,
                    budget.SkillId ?? "none",
                    budget.InstructionCharacters,
                    budget.RetrievalCharacters,
                    budget.MaxCharacters);
            }
        }
    }

    private async Task<AssistantApplicationContext?> StoredApplicationContextAsync(CancellationToken cancellationToken)
    {
        // One administration request measures every agent it lists against the same text.
        if (!_applicationContextRead)
        {
            _applicationContext = await personalization.GetApplicationContextAsync(cancellationToken);
            _applicationContextRead = true;
        }

        return _applicationContext;
    }

    private IReadOnlyList<NhAssistantInstructionBudget> Measure(
        NhAssistantAgent agent,
        AssistantApplicationContext? applicationContext)
    {
        var limit = registration.Limits.MaxInstructionCharacters;
        var retrieval = registration.AgentContexts.TryGetValue(agent.Id, out var context) && context.RetrievedContext is { } retrieved
            ? Math.Min(retrieved.MaxCharacters, registration.Limits.MaxRetrievedCharacters)
            : 0;
        var instructions = LongestInstructions(agent.Definition, applicationContext);
        var budgets = new List<NhAssistantInstructionBudget>
        {
            new(agent.Id, null, instructions, retrieval, limit)
        };
        if (registration.AgentRuns.TryGetValue(agent.Id, out var runs))
        {
            foreach (var skill in runs.Skills.Values.Where(skill => skill.Instructions is not null).OrderBy(skill => skill.Id, StringComparer.Ordinal))
            {
                budgets.Add(new NhAssistantInstructionBudget(
                    agent.Id,
                    skill.Id,
                    NhAssistantRetrievalChecks.WithSkill(instructions, skill),
                    retrieval,
                    limit));
            }
        }

        return budgets;
    }

    private int LongestInstructions(NhAssistantAgentDefinition agent, AssistantApplicationContext? applicationContext)
    {
        var guidance = NhAssistantAgentBuilderIds.IsAgentBuilder(agent.Id) ? registration.AgentBuilderGuidance : null;
        return Languages.Max(language => NhAssistantPromptComposer
            .Compose(agent, applicationContext, LongestPreferences, language, guidance)
            .Instructions.Length);
    }
}
