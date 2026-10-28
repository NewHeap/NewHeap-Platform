using Microsoft.Extensions.Options;

namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// The application's switch for compositions and the script tool (<see cref="NhAiCompositionFeature"/>):
/// whether they are available in one turn, run step or MCP request. The assistant asks it once per
/// feature at the start of each, so a value that changes meanwhile applies from the next one and a
/// running script never flips halfway. It compiles without the scripting package.
/// </summary>
/// <remarks>
/// <para>
/// The default reads <c>NewHeap:AI:Assistant:Compositions</c> and <c>NewHeap:AI:Assistant:Scripts</c>
/// (<see cref="NhAssistantOptions.Compositions"/>, <see cref="NhAssistantOptions.Scripts"/>), both off by
/// default. An application that offers the features as a setting of its own registers its switch with
/// <see cref="NhAssistantBuilder.UseCompositionSwitch{TSwitch}"/> and reads its own data, with a cache of
/// its own: the switch is a scoped service and is asked on every turn.
/// </para>
/// <para>
/// The switch only switches on what code allows: an agent through
/// <see cref="NhAssistantAgentContextBuilder.AllowCompositions"/> and
/// <see cref="NhAssistantAgentContextBuilder.AllowScripts"/>, an MCP exposure through its own
/// <c>AllowCompositions</c> and <c>AllowScripts</c>. It is not asked where code does not allow the
/// feature or no tool of the feature is registered. When it is off, the tools of the feature are not
/// offered to the model or listed over MCP, and a call that still arrives is refused with
/// <c>assistant-composition-off</c> or <c>assistant-scripting-off</c>; individual tools stay as they are.
/// A switch that throws counts as off for that turn; the exception type is logged.
/// </para>
/// </remarks>
public interface INhAssistantCompositionSwitch
{
    /// <summary>
    /// Whether <see cref="NhAssistantCompositionSwitchContext.Feature"/> is on for the turn, run step or
    /// MCP request that <paramref name="context"/> describes.
    /// </summary>
    ValueTask<bool> IsOnAsync(
        NhAssistantCompositionSwitchContext context,
        CancellationToken cancellationToken);
}

/// <summary>
/// Where the assistant asks the composition switch.
/// </summary>
public enum NhAssistantCompositionChannel
{
    /// <summary>
    /// A person's chat with the agent, also a message a person types in an agent's thread.
    /// </summary>
    Chat = 0,

    /// <summary>
    /// A run of the agent that a trigger, a schedule, a webhook or a person's start began.
    /// </summary>
    Run = 1,

    /// <summary>
    /// A consultation: another agent asked the agent a question.
    /// </summary>
    Consultation = 2,

    /// <summary>
    /// A run of the agent that another agent handed work to.
    /// </summary>
    HandOff = 3,

    /// <summary>
    /// A request of an MCP client to the application's MCP exposure (<c>WithNewHeapPlatformAITools</c>).
    /// </summary>
    Mcp = 4
}

/// <summary>
/// What the composition switch is asked about.
/// </summary>
/// <param name="Feature">Compositions or scripts; never <see cref="NhAiCompositionFeature.None"/>.</param>
/// <param name="Channel">Where the feature would be offered.</param>
public sealed record NhAssistantCompositionSwitchContext(
    NhAiCompositionFeature Feature,
    NhAssistantCompositionChannel Channel)
{
    /// <summary>
    /// The tenant of the turn, run or request, or <see langword="null"/> when it has none, as in a
    /// single-tenant application without tenant ids.
    /// </summary>
    public string? TenantId { get; init; }

    /// <summary>
    /// The agent the feature would be offered to; <see langword="null"/> for an MCP request, which no
    /// agent makes.
    /// </summary>
    public string? AgentId { get; init; }

    /// <summary>
    /// The person the work is for: the person who chats, the initiator of a run (none for a run that
    /// nobody started by hand), the person who asked in a consultation, the caller of an MCP request,
    /// and the reading person for the effective state (<see cref="INhAssistantCompositionStatus"/>).
    /// <see langword="null"/> when the work is for nobody in particular.
    /// </summary>
    public string? PersonActorId { get; init; }
}

/// <summary>
/// The default switch: <see cref="NhAssistantOptions.Compositions"/> and <see cref="NhAssistantOptions.Scripts"/>
/// as configuration has them now.
/// </summary>
internal sealed class NhAssistantConfigurationCompositionSwitch(
    IOptionsMonitor<NhAssistantOptions> options) : INhAssistantCompositionSwitch
{
    public ValueTask<bool> IsOnAsync(
        NhAssistantCompositionSwitchContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var current = options.CurrentValue;
        var on = context.Feature switch
        {
            NhAiCompositionFeature.Compositions => current.Compositions,
            NhAiCompositionFeature.Scripts => current.Scripts,
            _ => false
        };
        return ValueTask.FromResult(on);
    }
}
