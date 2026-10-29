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
}
