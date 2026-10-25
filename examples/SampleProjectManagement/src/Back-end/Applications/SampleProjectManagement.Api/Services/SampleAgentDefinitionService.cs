using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.Common.Models;
using SampleProjectManagement.Api.Composition;

namespace SampleProjectManagement.Api.Services;

/// <summary>
/// SPM-329: the portal's operations process reads its agents as they are in effect with
/// <see cref="INhAssistantAgentDirectory"/> and changes their definitions from code with
/// <see cref="INhAssistantAgentDefinitions"/>, with the checks, versions, package locks and audit of the
/// administration. It reads an agent and sends its definition back at the version it read, so a change
/// by someone else in the meantime is refused, and the agent keeps its MCP assignments because the
/// request names none. The directory authorizes nothing: the application calls it for an operator it
/// authorized. SPM-330: an operator promotes a shadow variant created as data onto its live agent once
/// the evaluation shows it does better, with its skills; the variant never goes live itself.
/// </summary>
public sealed class SampleAgentDefinitionService(
    INhAssistantAgentDirectory directory,
    INhAssistantAgentDefinitions definitions)
{
    /// <summary>
    /// The agents of the operations overview: every agent that is no shadow variant, as in effect,
    /// with its cost center when it has one.
    /// </summary>
    public async Task<IReadOnlyList<SampleAgentOverview>> OverviewAsync(CancellationToken cancellationToken)
    {
        var agents = await directory.ListAsync(new NhAssistantAgentDirectoryFilter { IncludeShadowVariants = false }, cancellationToken);
        return agents
            .Select(agent => new SampleAgentOverview(
                agent.AgentId,
                agent.Source,
                agent.DisplayName,
                agent.RunState,
                agent.IsOverridden,
                agent.InstructionsProblem,
                CostCenterOf(agent)))
            .ToArray();
    }

    /// <summary>
    /// Gives an agent a new description and leaves the rest of its definition as it is in effect: for
    /// an agent in code an override until it is reset.
    /// </summary>
    public async Task<TaskResult<NhAssistantAgentInEffectDto>> DescribeAsync(
        string agentId,
        string description,
        Guid operatorId,
        CancellationToken cancellationToken)
    {
        var agent = await directory.GetAsync(agentId, cancellationToken);
        if (agent is null)
        {
            return TaskResult<NhAssistantAgentInEffectDto>.Failed(NhAssistantErrorCodes.AgentNotFound, "The agent was not found.");
        }

        var request = new NhAssistantAgentDefinitionRequest(
            agent.DisplayName,
            description,
            agent.InstructionsAsset is null ? agent.Instructions ?? string.Empty : string.Empty,
            agent.ToolSelectors,
            agent.Autonomy,
            agent.RequiredPolicy)
        {
            InstructionsAsset = agent.InstructionsAsset
        };
        return await definitions.UpdateAsync(agentId, request, agent.Version, operatorId.ToString("D"), cancellationToken);
    }

    /// <summary>
    /// Puts an agent in code back to its code definition.
    /// </summary>
    public Task<TaskResult<NhAssistantAgentInEffectDto>> ResetAsync(string agentId, Guid operatorId, CancellationToken cancellationToken)
    {
        return definitions.ResetAsync(agentId, operatorId.ToString("D"), cancellationToken);
    }

    /// <summary>
    /// Retires an agent created at runtime; an agent in code stays and is switched off instead.
    /// </summary>
    public Task<TaskResult> RetireAsync(string agentId, Guid operatorId, CancellationToken cancellationToken)
    {
        return definitions.DeleteAsync(agentId, operatorId.ToString("D"), cancellationToken);
    }

    /// <summary>
    /// SPM-330: promotes a shadow variant created as data onto its live agent at the versions the
    /// operator evaluated, with its skills, and removes the variant.
    /// </summary>
    public async Task<TaskResult<NhAssistantShadowVariantPromotionDto>> PromoteAsync(
        string variantId,
        Guid operatorId,
        CancellationToken cancellationToken)
    {
        var variant = await directory.GetAsync(variantId, cancellationToken);
        if (variant?.ShadowOf is not { } liveAgentId || await directory.GetAsync(liveAgentId, cancellationToken) is not { } live)
        {
            return TaskResult<NhAssistantShadowVariantPromotionDto>.Failed(
                NhAssistantErrorCodes.NotShadowVariant,
                "Only a shadow variant created as data is promoted onto its live agent.");
        }

        return await definitions.PromoteShadowVariantAsync(
            variantId,
            new NhAssistantShadowVariantPromotionRequest(
                live.Version,
                MoveSkills: true,
                ExpectedVariantVersion: variant.Version,
                Reason: "The trial did better in the evaluation"),
            operatorId.ToString("D"),
            cancellationToken);
    }

    private static string? CostCenterOf(NhAssistantAgentInEffectDto agent)
    {
        var entry = agent.Metadata.FirstOrDefault(item => item.Name == SampleAgentCostCenterComposition.Entry);
        return entry?.IndexValue;
    }
}

/// <summary>
/// One agent of the operations overview.
/// </summary>
/// <param name="AgentId">The agent's id.</param>
/// <param name="Source"><c>code</c> or <c>admin</c>.</param>
/// <param name="DisplayName">Its name in effect, a translation key for an agent in code.</param>
/// <param name="RunState">Its run state, such as <c>active</c> or <c>off</c>.</param>
/// <param name="IsOverridden">An administrator overrides its definition in code.</param>
/// <param name="InstructionsProblem">Why its instructions are not available, when they come from an
/// asset that does not resolve.</param>
/// <param name="CostCenter">Its cost center's searchable code (SPM-313), in lowercase.</param>
public sealed record SampleAgentOverview(
    string AgentId,
    string Source,
    string DisplayName,
    string RunState,
    bool IsOverridden,
    string? InstructionsProblem,
    string? CostCenter);
