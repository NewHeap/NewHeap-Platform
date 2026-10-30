namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// Where the time of a turn went, so a slow turn can be explained: the turn's log line shows it, and
/// <c>NhAssistantConversationTurnResult.Timings</c> of <c>NewHeap.Platform.AI.Chat.AspNet</c> returns it.
/// It holds durations and counts only, never content. A turn that continues after an approval or a
/// question has timings of its own from that moment on, so the time a person takes counts for nobody.
/// </summary>
/// <param name="Total">From the message's arrival to the end of the turn.</param>
/// <param name="Preparation">From the message's arrival to the first model call: storing the message and
/// reading the history, the prompt, the tools, retrieval and files.</param>
/// <param name="Model">All model calls together, each from its start to its last update: what the
/// provider took.</param>
/// <param name="ModelCalls">The number of model calls.</param>
/// <param name="Tools">All tool calls together, with the governance around them.</param>
public sealed record NhAssistantTurnTimings(
    TimeSpan Total,
    TimeSpan Preparation,
    TimeSpan Model,
    int ModelCalls,
    TimeSpan Tools)
{
    /// <summary>
    /// The part of <see cref="Preparation"/> that went to connecting to the agent's MCP servers and
    /// listing their tools.
    /// </summary>
    public TimeSpan McpTools { get; init; }

    /// <summary>
    /// The part of <see cref="Preparation"/> that went to the agent's context sources.
    /// </summary>
    public TimeSpan Retrieval { get; init; }

    /// <summary>
    /// The time that went to opening the turn's files, in <see cref="Preparation"/> for what a message
    /// attached and in <see cref="Tools"/> for what a tool opened.
    /// </summary>
    public TimeSpan Files { get; init; }

    /// <summary>
    /// From the message's arrival to the first text of the answer, what a person waits for;
    /// <see langword="null"/> for a turn without text.
    /// </summary>
    public TimeSpan? FirstText { get; init; }
}
