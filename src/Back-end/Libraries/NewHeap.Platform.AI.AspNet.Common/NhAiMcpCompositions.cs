using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace NewHeap.Platform.AI.AspNet;

/// <summary>
/// The ceiling in code of the MCP exposure for compositions and the script tool
/// (<see cref="NhAiMcpExposureBuilder.AllowCompositions"/>, <see cref="NhAiMcpExposureBuilder.AllowScripts"/>).
/// One instance per host; without it the exposure allows neither.
/// </summary>
internal sealed class NhAiMcpCompositionCeiling
{
    public bool AllowCompositions { get; set; }

    public bool AllowScripts { get; set; }

    public bool Allows(NhAiCompositionFeature feature)
    {
        return feature switch
        {
            NhAiCompositionFeature.Compositions => AllowCompositions,
            NhAiCompositionFeature.Scripts => AllowScripts,
            _ => false
        };
    }
}

/// <summary>
/// Whether the application's switch has a composition feature on for one MCP request. The assistant
/// replaces the default with one that asks its <c>INhAssistantCompositionSwitch</c> for the MCP
/// channel.
/// </summary>
internal interface INhAiMcpCompositionGate
{
    ValueTask<bool> IsOnAsync(
        NhAiCompositionFeature feature,
        NhAiInvocationContext context,
        CancellationToken cancellationToken);
}

/// <summary>
/// The gate of a host without the assistant: <c>NewHeap:AI:Assistant:Compositions</c> and
/// <c>NewHeap:AI:Assistant:Scripts</c> as configuration has them now, both off without configuration.
/// </summary>
internal sealed class NhAiMcpConfigurationCompositionGate(IServiceProvider services) : INhAiMcpCompositionGate
{
    public const string CompositionsKey = "NewHeap:AI:Assistant:Compositions";
    public const string ScriptsKey = "NewHeap:AI:Assistant:Scripts";

    public ValueTask<bool> IsOnAsync(
        NhAiCompositionFeature feature,
        NhAiInvocationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var configuration = services.GetService<IConfiguration>();
        if (configuration is null)
        {
            return ValueTask.FromResult(false);
        }

        var key = feature switch
        {
            NhAiCompositionFeature.Compositions => CompositionsKey,
            NhAiCompositionFeature.Scripts => ScriptsKey,
            _ => null
        };
        return ValueTask.FromResult(key is not null && configuration.GetValue<bool>(key));
    }
}

/// <summary>
/// The compositions and the script tool of one MCP request (<c>tools/list</c> or <c>tools/call</c>): a
/// tool of a feature that the exposure does not allow in code is not part of the exposure, and one whose
/// feature the switch has off is not listed and refused when called. The switch is asked once per
/// feature and request, and only for a feature the exposure allows; one that throws counts as off.
/// </summary>
internal sealed class NhAiMcpCompositionRequest(IServiceProvider services, NhAiInvocationContext context)
{
    private readonly Dictionary<NhAiCompositionFeature, bool> _decided = [];

    /// <summary>
    /// Whether <paramref name="tool"/> takes part in the request: <see cref="NhAiMcpCompositionAvailability.Available"/>
    /// for an individual tool and a tool of a feature that is on.
    /// </summary>
    public async ValueTask<NhAiMcpCompositionAvailability> AvailabilityOfAsync(
        McpServerTool tool,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var feature = tool.Metadata.OfType<NhAiToolDescriptor>().FirstOrDefault()?.CompositionFeature
            ?? NhAiCompositionFeature.None;
        if (feature == NhAiCompositionFeature.None)
        {
            return NhAiMcpCompositionAvailability.Available;
        }

        var ceiling = services.GetService<NhAiMcpCompositionCeiling>();
        if (ceiling is null || !ceiling.Allows(feature))
        {
            return NhAiMcpCompositionAvailability.NotExposed;
        }

        if (!_decided.TryGetValue(feature, out var on))
        {
            on = await AskAsync(feature, cancellationToken);
            _decided[feature] = on;
        }

        return on ? NhAiMcpCompositionAvailability.Available : NhAiMcpCompositionAvailability.Off;
    }

    private async ValueTask<bool> AskAsync(NhAiCompositionFeature feature, CancellationToken cancellationToken)
    {
        var gate = services.GetService<INhAiMcpCompositionGate>();
        if (gate is null)
        {
            return false;
        }

        try
        {
            return await gate.IsOnAsync(feature, context, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            services.GetService<ILoggerFactory>()?
                .CreateLogger<NhAiMcpCompositionRequest>()
                .LogWarning(
                    "The composition switch failed with {ExceptionType} for {Feature} in an MCP request; the feature is off.",
                    exception.GetType().Name,
                    feature);
            return false;
        }
    }
}

/// <summary>
/// Whether a tool takes part in one MCP request.
/// </summary>
internal enum NhAiMcpCompositionAvailability
{
    /// <summary>An individual tool, or a composition or the script tool whose feature is on.</summary>
    Available = 0,

    /// <summary>The exposure does not allow the tool's feature in code: the tool is not part of it.</summary>
    NotExposed = 1,

    /// <summary>The switch has the tool's feature off for this request.</summary>
    Off = 2
}
