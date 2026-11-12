namespace NewHeap.Platform.AI.Chat.AGUI;

/// <summary>
/// The names of the AG-UI <c>CUSTOM</c> events the assistant's AG-UI endpoint
/// (<see cref="NhAssistantAGUIEndpointRouteBuilderExtensions.MapNewHeapAssistantAGUI"/>) writes for what the
/// AG-UI events do not carry themselves. A generic AG-UI client may ignore them; the run stays complete
/// without them. Their values hold the same contract objects as the assistant's own server-sent events.
/// </summary>
public static class NhAssistantAGUICustomEvents
{
    /// <summary>
    /// A proposal waits for a decision: the value is the approval part of the conversation, with its
    /// before and after presentation, targets, expiry and proposal hash. When the turn waits for it, the
    /// run also ends with a <c>tool_call</c> interrupt for it.
    /// </summary>
    public const string ApprovalRequired = "newheap.approval.required";

    /// <summary>
    /// The question the turn asked was answered: the value is the question part with the chosen answer.
    /// </summary>
    public const string QuestionAnswered = "newheap.question.answered";

    /// <summary>
    /// An MCP server asks the turn's person for input while its tool call waits, within the running turn:
    /// the value is an AG-UI interrupt object (<c>id</c>, <c>reason</c> <c>input_required</c>, <c>message</c>,
    /// <c>responseSchema</c>, <c>expiresAt</c>) whose <c>metadata</c> holds the elicitation part. The run does
    /// not end for it, because the server keeps its call open; the client answers it with a resume entry of
    /// that id in a request of its own to the same endpoint, while this run goes on.
    /// </summary>
    public const string ElicitationRequested = "newheap.elicitation.requested";

    /// <summary>
    /// The request for input was answered, declined, cancelled or expired: the value is the elicitation part.
    /// </summary>
    public const string ElicitationClosed = "newheap.elicitation.closed";

    /// <summary>
    /// The person has to connect an MCP server before the agent can use its tools; the turn continues without
    /// them. The value holds <c>serverId</c>, <c>displayName</c> and <c>reason</c>.
    /// </summary>
    public const string ConnectionRequired = "newheap.connection.required";
}
