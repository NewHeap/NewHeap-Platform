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
}
