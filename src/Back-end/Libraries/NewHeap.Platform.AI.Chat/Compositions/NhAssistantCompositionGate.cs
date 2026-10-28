using Microsoft.Extensions.Logging;

namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// Decides for a turn, run step or MCP request whether the tools of a composition feature may be
/// offered: code must allow the feature and the application's <see cref="INhAssistantCompositionSwitch"/>
/// must have it on. The switch is asked only where code allows the feature, and a switch that throws
/// counts as off.
/// </summary>
internal sealed class NhAssistantCompositionGate(
    INhAssistantCompositionSwitch compositionSwitch,
    NhAssistantRegistrationState registration,
    ILogger<NhAssistantCompositionGate> logger)
{
    /// <summary>
    /// Whether code allows <paramref name="agentId"/> <paramref name="feature"/>
    /// (<see cref="NhAssistantAgentContextBuilder.AllowCompositions"/>,
    /// <see cref="NhAssistantAgentContextBuilder.AllowScripts"/>). Agents created in the administration
    /// have no ceiling in code and are never allowed.
    /// </summary>
    public bool AllowsInCode(string agentId, NhAiCompositionFeature feature)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        return registration.AgentContexts.TryGetValue(agentId, out var options) && options.Allows(feature);
    }

    /// <summary>
    /// Asks the switch about <paramref name="context"/>. An exception of the switch other than the
    /// cancellation of <paramref name="cancellationToken"/> is logged by type and counts as off.
    /// </summary>
    public async ValueTask<bool> AskSwitchAsync(
        NhAssistantCompositionSwitchContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Feature is not (NhAiCompositionFeature.Compositions or NhAiCompositionFeature.Scripts))
        {
            throw new ArgumentOutOfRangeException(nameof(context), "The composition switch is asked about compositions or scripts only.");
        }

        try
        {
            return await compositionSwitch.IsOnAsync(context, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            logger.LogWarning(
                "The composition switch failed with {ExceptionType} for {Feature} in channel {Channel}; the feature is off.",
                exception.GetType().Name,
                context.Feature,
                context.Channel);
            return false;
        }
    }

    /// <summary>
    /// Whether the tools of <paramref name="feature"/> may be offered to <paramref name="agentId"/>:
    /// code allows the feature and the switch has it on for <paramref name="context"/>.
    /// </summary>
    public async ValueTask<bool> IsOfferedAsync(
        string agentId,
        NhAiCompositionFeature feature,
        NhAssistantCompositionSwitchContext context,
        CancellationToken cancellationToken)
    {
        if (!AllowsInCode(agentId, feature))
        {
            return false;
        }

        return await AskSwitchAsync(context, cancellationToken);
    }
}

/// <summary>
/// The decisions of one turn: per composition feature whether its tools are offered, decided once
/// when the turn first needs them and kept for the rest of the turn, its skill switches and its
/// closing answer included, so a switch that changes meanwhile applies from the next turn.
/// </summary>
internal sealed class NhAssistantTurnCompositions
{
    private readonly Dictionary<NhAiCompositionFeature, bool> _decided = [];

    /// <summary>
    /// The decision of <paramref name="feature"/> in this turn, or <see langword="null"/> before the
    /// turn needed it.
    /// </summary>
    public bool? DecisionOf(NhAiCompositionFeature feature)
    {
        return _decided.TryGetValue(feature, out var on) ? on : null;
    }

    /// <summary>
    /// The decision of <paramref name="feature"/>, made with <paramref name="decide"/> the first time.
    /// </summary>
    public async ValueTask<bool> DecideAsync(NhAiCompositionFeature feature, Func<ValueTask<bool>> decide)
    {
        ArgumentNullException.ThrowIfNull(decide);
        if (_decided.TryGetValue(feature, out var on))
        {
            return on;
        }

        on = await decide();
        _decided[feature] = on;
        return on;
    }
}
