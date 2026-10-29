using Microsoft.Extensions.Options;

namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// The one gate of agent work on this host (FB17): whether the assistant is enabled and, while it is,
/// whether <see cref="NhAssistantOptions.AgentWork"/> lets agents work. Every kind of agent work asks it
/// before it starts: triggers, schedules, webhooks, starts by hand, replays, runs, executions of approved
/// proposals, consultations, hand-offs, follow-ups and the application's messages. A person's turn in a
/// chat or in an agent's thread is no agent work and never asks it; it needs the assistant enabled only.
/// Neither is recording a proposal the application makes for a person without a run (FB21), whose
/// execution once approved is agent work and asks it. The options are read on every call, so a reloaded
/// configuration applies to the next piece of work.
/// </summary>
internal static class NhAssistantAgentWorkGate
{
    /// <summary>
    /// Why agents may not work now: <see cref="NhAssistantErrorCodes.Disabled"/> while the assistant is
    /// disabled, <see cref="NhAssistantErrorCodes.AgentWorkOff"/> while it is enabled with agent work
    /// off; <see langword="null"/> when agents work.
    /// </summary>
    internal static string? RefusalOf(NhAssistantOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled)
        {
            return NhAssistantErrorCodes.Disabled;
        }
        if (!options.AgentWork)
        {
            return NhAssistantErrorCodes.AgentWorkOff;
        }

        return null;
    }

    /// <summary>
    /// <see cref="RefusalOf(NhAssistantOptions)"/> with the options in effect now.
    /// </summary>
    internal static string? RefusalOf(IOptionsMonitor<NhAssistantOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return RefusalOf(options.CurrentValue);
    }

    /// <summary>
    /// Whether agents work now: the assistant is enabled and agent work is on.
    /// </summary>
    internal static bool IsOn(NhAssistantOptions options)
    {
        return RefusalOf(options) is null;
    }

    /// <summary>
    /// Whether agents work now, with the options in effect now.
    /// </summary>
    internal static bool IsOn(IOptionsMonitor<NhAssistantOptions> options)
    {
        return RefusalOf(options) is null;
    }

    /// <summary>
    /// Whether <paramref name="code"/> is a refusal of the whole host rather than of one agent: the
    /// assistant disabled or agent work off. Such a refusal of an event or a start is not audited per
    /// agent, as it would record every event the application publishes while it lasts.
    /// </summary>
    internal static bool IsHostRefusal(string? code)
    {
        return code is NhAssistantErrorCodes.Disabled or NhAssistantErrorCodes.AgentWorkOff;
    }
}
