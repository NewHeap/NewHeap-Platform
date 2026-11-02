namespace NewHeap.Platform.AI.Chat.Entities;

/// <summary>
/// One version of an administrator's switch that lets the language model of one agent raise alerts of
/// one kind (FB87-9), which the kind allows in code (<see cref="NhAssistantAlertKind.ModelTemplate"/>).
/// Versions are append-only: the highest version of an agent and a kind is the switch in effect, and the
/// earlier ones say who switched what when. A switch applies only to the template it was made for
/// (<see cref="ContractHash"/>); a kind whose template changed reads as off until it is switched again,
/// first in shadow.
/// </summary>
public sealed class AssistantAgentAlertModelSwitch
{
    public const int MaxReasonLength = 500;

    public Guid Id { get; set; }

    public string AgentId { get; set; } = string.Empty;

    /// <summary>The alert kind, registered with <c>AddAlertKind</c>.</summary>
    public string KindId { get; set; } = string.Empty;

    /// <summary>The version, from 1, per agent and kind.</summary>
    public int Version { get; set; }

    /// <summary>One of <see cref="NhAssistantAlertModelModes"/>.</summary>
    public string Mode { get; set; } = NhAssistantAlertModelModes.Off;

    /// <summary>The hash of the kind's template for the model when the switch was made.</summary>
    public string ContractHash { get; set; } = string.Empty;

    public string ChangedBy { get; set; } = string.Empty;

    public DateTimeOffset ChangedAt { get; set; }

    /// <summary>Why, kept with the switch and left out of the business audit.</summary>
    public string? Reason { get; set; }
}

/// <summary>
/// The modes of a switch that lets an agent's model raise alerts of a kind (FB87-9), as the
/// administration API names them.
/// </summary>
public static class NhAssistantAlertModelModes
{
    /// <summary>The agent's model is not offered the kind. The default.</summary>
    public const string Off = "off";

    /// <summary>
    /// The model raises the kind as shadow alerts: recorded with what the model filled in, delivered to
    /// nobody, and counted as "would have sent" in the kind's numbers.
    /// </summary>
    public const string Shadow = "shadow";

    /// <summary>The model's alerts reach the people the kind chooses, checked as every alert's recipients are.</summary>
    public const string Live = "live";

    public static bool IsKnown(string? mode)
    {
        return mode is Off or Shadow or Live;
    }
}
