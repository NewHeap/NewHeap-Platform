namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// The states of a replay of recorded runs into a shadow variant.
/// </summary>
public static class NhAssistantReplayStatuses
{
    /// <summary>The replay fires the recorded runs into the variant, a few at a time.</summary>
    public const string Running = "running";

    /// <summary>Every run of the period, up to the replay's maximum, was fired.</summary>
    public const string Completed = "completed";

    /// <summary>The variant's budget, run limit or state stopped the replay; its stop reason says which.</summary>
    public const string Stopped = "stopped";

    public static bool IsKnown(string status)
    {
        return status is Running or Completed or Stopped;
    }
}

/// <summary>
/// Bounds of evaluations and replays.
/// </summary>
public static class NhAssistantEvaluationLimits
{
    /// <summary>
    /// The most runs one replay fires into a variant.
    /// </summary>
    public const int MaxReplayRuns = 500;

    /// <summary>
    /// The most runs of each agent one evaluation reads; a longer period is reported as truncated.
    /// </summary>
    public const int MaxEvaluatedRuns = 5_000;

    /// <summary>
    /// The longest period one evaluation or replay covers.
    /// </summary>
    public static TimeSpan MaxPeriod { get; } = TimeSpan.FromDays(92);
}
