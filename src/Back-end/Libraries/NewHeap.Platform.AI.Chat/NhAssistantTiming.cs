namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// Fixed waits of the assistant. The assistant reads a registered instance, otherwise
/// <see cref="Default"/>; test hosts register shorter waits.
/// </summary>
internal sealed class NhAssistantTiming
{
    internal static NhAssistantTiming Default { get; } = new();

    /// <summary>
    /// The wait before the one retry of a model call that failed transiently before it produced anything.
    /// </summary>
    internal TimeSpan TransientProviderRetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How often a caller that waits for a turn reads the conversation: the turn may run on another
    /// host, whose events do not reach this one, and the caller may lose access while it waits.
    /// </summary>
    internal TimeSpan TurnWaitCheckInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long the run configuration in effect is trusted before a fire or a run start reads its
    /// stamp again.
    /// </summary>
    internal TimeSpan RunConfigurationCheckInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long an instruction asset that the application's source failed to return stays unavailable
    /// before a read or a check of the run configuration asks the source again.
    /// </summary>
    internal TimeSpan InstructionAssetRetryInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a person's chat waits after an execution report before its follow-up turn starts
    /// (<see cref="NhAssistantApplicationExecutionBuilder.ContinueChatAfterReport"/>), so reports that
    /// arrive together reach one turn. It is a debounce, so it runs on the application's clock.
    /// </summary>
    internal TimeSpan ChatFollowUpDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How often the assistant hub validates the session of an open connection again, on the
    /// application's clock: every minute, as NewHeap's library hubs do.
    /// </summary>
    internal TimeSpan HubSessionCheckInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The clock of a turn's own timeout, of the time the turn stands still while its person answers an
    /// MCP server's request for input, and of the heartbeat meanwhile: the system clock, as turns,
    /// leases and heartbeats keep it. A test host registers a manual clock, so a person's minutes pass
    /// without waiting.
    /// </summary>
    internal TimeProvider TurnClock { get; init; } = TimeProvider.System;

    /// <summary>
    /// How often a turn that stands still for its person renews its claim on the conversation, so that
    /// no other request takes the conversation over as abandoned: well within the minute that a running
    /// conversation is given beyond the turn timeout.
    /// </summary>
    internal TimeSpan TurnHeartbeatInterval { get; init; } = TimeSpan.FromSeconds(30);
}
