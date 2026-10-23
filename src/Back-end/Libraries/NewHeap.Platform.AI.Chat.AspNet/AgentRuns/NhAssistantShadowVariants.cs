using NewHeap.Platform.AI.Chat.Persistence;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// The shadow variants of one live agent, in code and created as data, and whether administrators
/// may create more.
/// </summary>
/// <param name="AllowsDataVariants">Whether the ceiling (<c>UseShadowVariantsAsData</c>) lets
/// administrators create variants of the agent as data and the agent is a live agent with a run
/// configuration in effect.</param>
/// <param name="MaxDataVariants">The most data variants the agent may have; 0 when it may have none.</param>
/// <param name="Variants">The variants in code first, then the data variants, each ordered by id.</param>
public sealed record NhAssistantShadowVariantsDto(
    string AgentId,
    bool AllowsDataVariants,
    int MaxDataVariants,
    IReadOnlyList<NhAssistantShadowVariantDto> Variants);

/// <param name="Source"><c>code</c> for <c>ShadowOf</c>, <c>data</c> for a variant created in the
/// administration; see <see cref="NhAssistantShadowVariantSources"/>.</param>
/// <param name="HasRunSettings">Whether the variant has run settings; a data variant receives the live
/// agent's triggers only with them. Always <see langword="true"/> for a variant in code.</param>
/// <param name="InEffect">Whether the variant receives the live agent's triggers now.</param>
/// <param name="Problem">Why a data variant with run settings is out of effect, one of
/// <see cref="NhAssistantRunConfigurationProblems"/>.</param>
public sealed record NhAssistantShadowVariantDto(
    string AgentId,
    string DisplayName,
    string Source,
    bool HasRunSettings,
    bool InEffect,
    string? Problem);

/// <summary>
/// Lists the shadow variants of a live agent for the administration.
/// </summary>
internal sealed class NhAssistantShadowVariants(
    NhAssistantRegistrationState registration,
    NhAssistantRunConfigurations configurations,
    NhAssistantRunConfigurationStore store,
    NhAssistantRunConfigurationValidator validator,
    NhAssistantAgentCatalog agents)
{
    /// <summary>
    /// The variants of <paramref name="liveAgentId"/>; <see langword="null"/> for an agent that does
    /// not exist.
    /// </summary>
    public async Task<NhAssistantShadowVariantsDto?> ListAsync(string liveAgentId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(liveAgentId);
        var all = await agents.ListAsync(includeDisabled: true, cancellationToken);
        if (all.All(agent => !string.Equals(agent.Id, liveAgentId, StringComparison.Ordinal)))
        {
            return null;
        }

        await configurations.EnsureCurrentAsync(cancellationToken);
        var names = all.ToDictionary(agent => agent.Id, agent => agent.Definition.DisplayNameKey, StringComparer.Ordinal);
        var problems = configurations.Problems;
        var variants = new List<NhAssistantShadowVariantDto>();
        foreach (var code in registration.CodeAgentRuns.Values
                     .Where(options => string.Equals(options.ShadowOfAgentId, liveAgentId, StringComparison.Ordinal))
                     .OrderBy(options => options.AgentId, StringComparer.Ordinal))
        {
            variants.Add(new NhAssistantShadowVariantDto(
                code.AgentId,
                names.GetValueOrDefault(code.AgentId) ?? code.AgentId,
                NhAssistantShadowVariantSources.Code,
                true,
                true,
                null));
        }

        var stored = await store.ListShadowVariantsAsync(liveAgentId, cancellationToken);
        foreach (var variant in stored)
        {
            var problem = problems
                .Where(entry => entry.AgentId == variant.AgentId && entry.Kind == NhAssistantRunConfigurationProblems.AgentKind)
                .Select(entry => entry.Code)
                .FirstOrDefault();
            var hasRunSettings = problem is not null || registration.AgentRuns.ContainsKey(variant.AgentId);
            variants.Add(new NhAssistantShadowVariantDto(
                variant.AgentId,
                variant.DisplayName,
                NhAssistantShadowVariantSources.Data,
                hasRunSettings,
                registration.AgentRuns.TryGetValue(variant.AgentId, out var options)
                && string.Equals(options.ShadowOfAgentId, liveAgentId, StringComparison.Ordinal),
                problem));
        }

        // A new variant of the agent would pass the checks of every load.
        var allowed = validator.CheckLiveAgentOfVariants(liveAgentId, registration.AgentRuns) is null;
        return new NhAssistantShadowVariantsDto(
            liveAgentId,
            allowed,
            allowed ? registration.ShadowVariantsAsData!.MaxPerAgent : 0,
            variants);
    }
}
