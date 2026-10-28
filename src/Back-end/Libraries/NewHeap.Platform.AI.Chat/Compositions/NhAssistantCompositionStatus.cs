namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// What the assistant actually does with compositions and the script tool for one agent, and why: the
/// effective state an application shows next to its own setting, and the administration's agent page.
/// <c>GET admin/agents/{id}/compositions</c> returns it behind the administration policy; this read
/// checks no rights itself.
/// </summary>
public interface INhAssistantCompositionStatus
{
    /// <summary>
    /// The state of both features for <paramref name="agentId"/>, per channel, as code and the
    /// application's switch have it now for <paramref name="request"/>; <see langword="null"/> for an
    /// agent that does not exist. The switch is asked once per feature and channel, only where the
    /// feature is installed and code allows it.
    /// </summary>
    Task<NhAssistantCompositionStatusDto?> GetAsync(
        string agentId,
        NhAssistantCompositionStatusRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// For whom the effective state is read: the switch gets this tenant and person.
/// </summary>
public sealed record NhAssistantCompositionStatusRequest(string? TenantId, string? PersonActorId);

/// <summary>
/// The effective state of compositions and scripts for one agent.
/// </summary>
/// <param name="Features">Compositions, then scripts.</param>
public sealed record NhAssistantCompositionStatusDto(
    string AgentId,
    IReadOnlyList<NhAssistantCompositionFeatureStatusDto> Features);

/// <summary>
/// The effective state of one feature for an agent.
/// </summary>
/// <param name="Feature"><c>compositions</c> or <c>scripts</c> (<see cref="NhAiCompositionFeatures.NameOf"/>).</param>
/// <param name="On">Whether the feature is on in at least one channel.</param>
/// <param name="Reason">One of <see cref="NhAssistantCompositionReasons"/>: <c>on</c> while the feature is on
/// in a channel, otherwise why it is off everywhere: <c>not-installed</c>, then for an agent in code
/// <c>agent-not-allowed</c> and for an agent created in the administration
/// <c>not-allowed-for-data-agents</c> or <c>off-for-agent</c>, then <c>off-by-application</c>.</param>
/// <param name="Channels">Chat, run, consultation and hand-off, each with its own state.</param>
public sealed record NhAssistantCompositionFeatureStatusDto(
    string Feature,
    bool On,
    string Reason,
    IReadOnlyList<NhAssistantCompositionChannelStatusDto> Channels);

/// <summary>
/// The effective state of one feature for an agent in one channel.
/// </summary>
/// <param name="Channel">One of <see cref="NhAssistantCompositionChannels"/>.</param>
/// <param name="Reason">One of <see cref="NhAssistantCompositionReasons"/>.</param>
public sealed record NhAssistantCompositionChannelStatusDto(
    string Channel,
    bool On,
    string Reason);

/// <summary>
/// Why a composition feature is on or off for an agent.
/// </summary>
public static class NhAssistantCompositionReasons
{
    /// <summary>Code allows the feature and the application's switch has it on.</summary>
    public const string On = "on";

    /// <summary>
    /// No tool of the feature is registered: for scripts the scripting package is not installed, for
    /// compositions the application declares none.
    /// </summary>
    public const string NotInstalled = "not-installed";

    /// <summary>
    /// Code does not allow the agent the feature (<see cref="NhAssistantAgentContextBuilder.AllowCompositions"/>,
    /// <see cref="NhAssistantAgentContextBuilder.AllowScripts"/>); agents created in the administration never do.
    /// </summary>
    public const string AgentNotAllowed = "agent-not-allowed";

    /// <summary>The application's switch has the feature off (<see cref="INhAssistantCompositionSwitch"/>).</summary>
    public const string OffByApplication = "off-by-application";

    /// <summary>
    /// The agent was created in the administration and the application's ceiling
    /// (<see cref="NhAssistantBuilder.UseCompositionsAsData"/>) does not allow the feature for such agents.
    /// </summary>
    public const string NotAllowedForDataAgents = "not-allowed-for-data-agents";

    /// <summary>
    /// The agent was created in the administration, the application's ceiling allows the feature, and
    /// an administrator has not switched it on for this agent.
    /// </summary>
    public const string OffForAgent = "off-for-agent";
}

/// <summary>
/// The names of <see cref="NhAssistantCompositionChannel"/> in JSON.
/// </summary>
public static class NhAssistantCompositionChannels
{
    public const string Chat = "chat";
    public const string Run = "run";
    public const string Consultation = "consultation";
    public const string HandOff = "hand-off";
    public const string Mcp = "mcp";

    /// <summary>
    /// The JSON name of <paramref name="channel"/>.
    /// </summary>
    public static string NameOf(NhAssistantCompositionChannel channel)
    {
        return channel switch
        {
            NhAssistantCompositionChannel.Chat => Chat,
            NhAssistantCompositionChannel.Run => Run,
            NhAssistantCompositionChannel.Consultation => Consultation,
            NhAssistantCompositionChannel.HandOff => HandOff,
            NhAssistantCompositionChannel.Mcp => Mcp,
            _ => throw new ArgumentOutOfRangeException(nameof(channel))
        };
    }
}

internal sealed class NhAssistantCompositionStatus(
    NhAssistantAgentCatalog agents,
    NhAssistantCompositionGate gate,
    IEnumerable<INhAiToolCatalog> catalogs) : INhAssistantCompositionStatus
{
    /// <summary>
    /// The channels an agent works in; MCP requests are made to the exposure, not to an agent.
    /// </summary>
    private static readonly NhAssistantCompositionChannel[] AgentChannels =
    [
        NhAssistantCompositionChannel.Chat,
        NhAssistantCompositionChannel.Run,
        NhAssistantCompositionChannel.Consultation,
        NhAssistantCompositionChannel.HandOff
    ];

    private readonly INhAiToolCatalog[] _catalogs = catalogs.ToArray();

    public async Task<NhAssistantCompositionStatusDto?> GetAsync(
        string agentId,
        NhAssistantCompositionStatusRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!NhAssistantNames.IsSegment(agentId))
        {
            return null;
        }

        var agent = await agents.FindAsync(agentId, includeDisabled: true, cancellationToken);
        if (agent is null)
        {
            return null;
        }

        var features = new List<NhAssistantCompositionFeatureStatusDto>(NhAiCompositionFeatures.All.Count);
        foreach (var feature in NhAiCompositionFeatures.All)
        {
            features.Add(await FeatureStatusAsync(agent, feature, request, cancellationToken));
        }

        return new NhAssistantCompositionStatusDto(agent.Id, features);
    }

    private async Task<NhAssistantCompositionFeatureStatusDto> FeatureStatusAsync(
        NhAssistantAgent agent,
        NhAiCompositionFeature feature,
        NhAssistantCompositionStatusRequest request,
        CancellationToken cancellationToken)
    {
        var name = NhAiCompositionFeatures.NameOf(feature);
        string? blocked = null;
        if (!IsInstalled(feature))
        {
            blocked = NhAssistantCompositionReasons.NotInstalled;
        }
        else
        {
            blocked = gate.ReasonOf(agent, feature);
        }

        var channels = new List<NhAssistantCompositionChannelStatusDto>(AgentChannels.Length);
        foreach (var channel in AgentChannels)
        {
            if (blocked is not null)
            {
                channels.Add(new NhAssistantCompositionChannelStatusDto(NhAssistantCompositionChannels.NameOf(channel), false, blocked));
                continue;
            }

            var on = await gate.AskSwitchAsync(
                new NhAssistantCompositionSwitchContext(feature, channel)
                {
                    TenantId = request.TenantId,
                    AgentId = agent.Id,
                    PersonActorId = request.PersonActorId
                },
                cancellationToken);
            channels.Add(new NhAssistantCompositionChannelStatusDto(
                NhAssistantCompositionChannels.NameOf(channel),
                on,
                on ? NhAssistantCompositionReasons.On : NhAssistantCompositionReasons.OffByApplication));
        }

        var anyOn = channels.Any(channel => channel.On);
        var reason = anyOn
            ? NhAssistantCompositionReasons.On
            : blocked ?? NhAssistantCompositionReasons.OffByApplication;
        return new NhAssistantCompositionFeatureStatusDto(name, anyOn, reason, channels);
    }

    /// <summary>
    /// Whether a catalog registers a tool of <paramref name="feature"/> that agents can be offered.
    /// </summary>
    private bool IsInstalled(NhAiCompositionFeature feature)
    {
        return _catalogs
            .Select(NhAiToolCatalogViews.Of)
            .SelectMany(catalog => catalog.Descriptors)
            .Any(descriptor => descriptor.CompositionFeature == feature
                && descriptor.Exposure.HasFlag(NhAiToolExposure.Agent));
    }
}
