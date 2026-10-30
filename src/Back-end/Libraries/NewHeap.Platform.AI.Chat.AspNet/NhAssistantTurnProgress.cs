using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.Chat.Runtime;

namespace NewHeap.Platform.AI.Chat.AspNet;

/// <summary>
/// The kinds of <see cref="NhAssistantTurnProgress"/>.
/// </summary>
public static class NhAssistantTurnProgressKinds
{
    /// <summary>The turn started working on the message.</summary>
    public const string Started = "started";

    /// <summary>The answer grew by <see cref="NhAssistantTurnProgress.Text"/>.</summary>
    public const string Text = "text";

    /// <summary>A tool started; <see cref="NhAssistantTurnProgress.Tool"/> names it.</summary>
    public const string ToolStarted = "tool-started";

    /// <summary>A tool ended; <see cref="NhAssistantTurnProgress.Tool"/> names it and how it ended.</summary>
    public const string ToolCompleted = "tool-completed";

    /// <summary>
    /// The turn ended or waits for a person; <see cref="NhAssistantTurnProgress.Status"/> says which. It
    /// is the last progress of a turn.
    /// </summary>
    public const string Ended = "ended";
}

/// <summary>
/// One step of a running turn, as <see cref="INhAssistantConversationTurns"/> reports it to
/// <see cref="NhAssistantConversationTurnOptions.Progress"/> and <see cref="NhAssistantTurnWait.Progress"/>,
/// so a channel can show activity instead of silence. It carries the answer's text, which the person may
/// read anyway, and what a tool is called, never a tool's arguments or result. Pass text on sparingly: it
/// arrives per fragment of the model's stream, and a channel such as MCP does better with one update per
/// tool and a few for the text.
/// </summary>
/// <param name="Kind">One of <see cref="NhAssistantTurnProgressKinds"/>.</param>
public sealed record NhAssistantTurnProgress(string Kind)
{
    /// <summary>
    /// The turn, once it is known; <see cref="Guid.Empty"/> before its start reached a caller that came
    /// later.
    /// </summary>
    public Guid TurnId { get; init; }

    /// <summary>
    /// The new text of a <see cref="NhAssistantTurnProgressKinds.Text"/> step, not the whole answer.
    /// </summary>
    public string? Text { get; init; }

    /// <summary>
    /// The tool of a <see cref="NhAssistantTurnProgressKinds.ToolStarted"/> or
    /// <see cref="NhAssistantTurnProgressKinds.ToolCompleted"/> step.
    /// </summary>
    public NhAssistantTurnProgressTool? Tool { get; init; }

    /// <summary>
    /// How an <see cref="NhAssistantTurnProgressKinds.Ended"/> step ended: one of
    /// <see cref="NhAssistantTurnStatuses"/>.
    /// </summary>
    public string? Status { get; init; }

    /// <summary>
    /// The code of an <see cref="NhAssistantTurnProgressKinds.Ended"/> step that failed or reached a
    /// limit; <see langword="null"/> otherwise.
    /// </summary>
    public string? ErrorCode { get; init; }
}

/// <summary>
/// The tool of a progress step.
/// </summary>
/// <param name="InvocationId">The call, the same on its start and its end.</param>
/// <param name="ToolId">The tool's id; empty on a call's end, whose start named it.</param>
/// <param name="DisplayName">The tool's name in the turn's language; empty on a call's end.</param>
public sealed record NhAssistantTurnProgressTool(Guid InvocationId, string ToolId, string DisplayName)
{
    /// <summary>
    /// How the call ended, one of <see cref="NhAssistantToolCallStatuses"/>; <see langword="null"/> at its
    /// start.
    /// </summary>
    public string? Status { get; init; }

    /// <summary>
    /// The safe result code of a call that ended; <see langword="null"/> at its start or without one.
    /// </summary>
    public string? ResultCode { get; init; }
}

/// <summary>
/// Hands a turn's events to the application's <see cref="IProgress{T}"/>. Reporting never stops the turn
/// or the wait: a handler that throws is logged once and asked no further.
/// </summary>
internal sealed class NhAssistantTurnProgressReporter(IProgress<NhAssistantTurnProgress>? progress, ILogger logger)
{
    private IProgress<NhAssistantTurnProgress>? _progress = progress;

    public void Report(NhAssistantTurnEvent evt, Guid turnId)
    {
        if (_progress is not { } current || Translate(evt, turnId) is not { } step)
        {
            return;
        }

        try
        {
            current.Report(step);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _progress = null;
            logger.LogWarning(
                "A progress handler of assistant turn {TurnId} failed with {ExceptionType}; it gets no further progress.",
                turnId,
                exception.GetType().Name);
        }
    }

    private static NhAssistantTurnProgress? Translate(NhAssistantTurnEvent evt, Guid turnId)
    {
        return evt switch
        {
            NhAssistantTurnStartedEvent started => new NhAssistantTurnProgress(NhAssistantTurnProgressKinds.Started)
            {
                TurnId = started.TurnId
            },
            NhAssistantMessageDeltaEvent delta => new NhAssistantTurnProgress(NhAssistantTurnProgressKinds.Text)
            {
                TurnId = turnId,
                Text = delta.Text
            },
            NhAssistantToolStartedEvent tool => new NhAssistantTurnProgress(NhAssistantTurnProgressKinds.ToolStarted)
            {
                TurnId = turnId,
                Tool = new NhAssistantTurnProgressTool(tool.InvocationId, tool.ToolId, tool.DisplayName)
            },
            NhAssistantToolCompletedEvent tool => new NhAssistantTurnProgress(NhAssistantTurnProgressKinds.ToolCompleted)
            {
                TurnId = turnId,
                Tool = new NhAssistantTurnProgressTool(tool.InvocationId, string.Empty, string.Empty)
                {
                    Status = tool.Status,
                    ResultCode = tool.ResultCode
                }
            },
            NhAssistantTurnCompletedEvent completed => new NhAssistantTurnProgress(NhAssistantTurnProgressKinds.Ended)
            {
                TurnId = completed.TurnId,
                Status = completed.Status,
                ErrorCode = completed.ErrorCode
            },
            NhAssistantErrorEvent failed => new NhAssistantTurnProgress(NhAssistantTurnProgressKinds.Ended)
            {
                TurnId = turnId,
                Status = NhAssistantTurnStatuses.Failed,
                ErrorCode = failed.Code
            },
            _ => null
        };
    }
}
