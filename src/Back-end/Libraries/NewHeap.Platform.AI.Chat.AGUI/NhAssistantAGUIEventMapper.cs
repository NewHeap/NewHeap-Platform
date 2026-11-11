using System.Text.Json;
using System.Text.Json.Nodes;
using AGUI.Abstractions;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.Runtime;

namespace NewHeap.Platform.AI.Chat.AGUI;

/// <summary>
/// Translates the events of one assistant turn into the events of one AG-UI run, the way the assistant's own
/// server-sent events translate them (<see cref="NhAssistantServerSentEventsResult"/>): the same previews,
/// the same trusted result cards and never a shielded result.
/// <list type="bullet">
/// <item><c>turn.started</c>, <c>turn.completed</c> and <c>error</c> open and close the run; an error is a
/// <c>RUN_ERROR</c> with the assistant's content-free code.</item>
/// <item>Text streams as text messages; a tool call ends the open one, and text after it opens the next.</item>
/// <item>A server tool call is <c>TOOL_CALL_START</c>/<c>ARGS</c>/<c>END</c> with the arguments' preview and
/// <c>TOOL_CALL_RESULT</c> with the completed call as the assistant reports it, result card included.</item>
/// <item>A client tool (<c>AddClientTool</c>) is a frontend tool call with its bound arguments, which the AG-UI
/// client runs; the run ends without an interrupt and the client's tool message reports its result.</item>
/// <item>A proposal or question the turn waits for ends the run with an AG-UI interrupt.</item>
/// <item>The agent's plan (<c>plan.updated</c>) is a <c>STATE_SNAPSHOT</c> whose state is <c>{ "plan": { turnId,
/// messageId, steps, updatedAt } }</c>: the server owns this state, and a client's own <c>state</c> never reaches
/// the turn.</item>
/// </list>
/// One instance serves one run; it is not thread-safe.
/// </summary>
internal sealed class NhAssistantAGUIEventMapper(
    string threadId,
    string runId,
    Guid conversationId,
    NhAssistantRegistrationState registration)
{
    private const string Assistant = "assistant";

    private readonly Dictionary<Guid, NhAssistantToolStartedEvent> _deferredClientTools = [];
    private readonly HashSet<Guid> _reportedCalls = [];
    private string? _openText;
    private int _textSegments;
    private bool _started;
    private bool _finished;
    private object? _lastWait;

    /// <summary>
    /// The client tool call whose result the client reported in this run's request: its result is the
    /// client's own tool message, so the run does not report it again.
    /// </summary>
    public Guid? ReportedClientTool { get; init; }

    /// <summary>
    /// The tool call of the proposal this run's request decided: the client knows it from the interrupted run,
    /// so this run reports only its result, as an AG-UI tool approval expects.
    /// </summary>
    public Guid? DecidedCall { get; init; }

    /// <summary>
    /// Whether the run already ended with <c>RUN_FINISHED</c> or <c>RUN_ERROR</c>.
    /// </summary>
    public bool IsFinished => _finished;

    /// <summary>
    /// The AG-UI events of <paramref name="evt"/>; none after the run ended.
    /// </summary>
    public IReadOnlyList<BaseEvent> Map(NhAssistantTurnEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        var events = new List<BaseEvent>();
        if (_finished)
        {
            return events;
        }

        Start(events, evt as NhAssistantTurnStartedEvent);
        events.AddRange(evt switch
        {
            // Opens the run, which Start already wrote.
            NhAssistantTurnStartedEvent => [],
            NhAssistantMessageDeltaEvent delta => Text(delta),
            NhAssistantToolStartedEvent tool => ToolStarted(tool),
            NhAssistantToolCompletedEvent tool => ToolCompleted(tool),
            NhAssistantApprovalRequiredEvent approval => ApprovalRequired(approval),
            NhAssistantQuestionAskedEvent asked => QuestionAsked(asked),
            NhAssistantQuestionAnsweredEvent answered => Custom(
                NhAssistantAGUICustomEvents.QuestionAnswered,
                Part(answered.Question)),
            NhAssistantClientToolRequestedEvent requested => ClientToolRequested(requested),
            NhAssistantPlanUpdatedEvent plan => PlanUpdated(plan),
            NhAssistantElicitationRequestedEvent requested => Custom(
                NhAssistantAGUICustomEvents.ElicitationRequested,
                JsonSerializer.SerializeToElement(
                    NhAssistantAGUIInterrupts.Elicitation(requested.Elicitation),
                    AGUIJsonSerializerContext.Default.AGUIInterrupt)),
            NhAssistantElicitationClosedEvent closed => Custom(
                NhAssistantAGUICustomEvents.ElicitationClosed,
                Part(closed.Elicitation)),
            NhAssistantConnectionRequiredEvent connection => Custom(
                NhAssistantAGUICustomEvents.ConnectionRequired,
                JsonSerializer.SerializeToElement(
                    new NhAssistantConnectionRequiredDto(connection.ServerId, connection.DisplayName, connection.Reason),
                    NhAssistantJsonSerializerContext.Default.NhAssistantConnectionRequiredDto)),
            NhAssistantTurnCompletedEvent completed => Completed(completed),
            NhAssistantErrorEvent error => Failed(error.Code)
        });
        return events;
    }

    /// <summary>
    /// The events that end a run whose turn stream ended without <c>turn.completed</c> or <c>error</c>: it
    /// reads as cancelled.
    /// </summary>
    public IReadOnlyList<BaseEvent> Finish()
    {
        var events = new List<BaseEvent>();
        if (_finished)
        {
            return events;
        }

        Start(events, null);
        events.AddRange(CloseText());
        events.Add(Finished(new RunFinishedCancelledOutcome(), null));
        return events;
    }

    /// <summary>
    /// A run that starts no turn, such as the answer to an MCP server's request for input, which the running
    /// turn's own run continues with, or a cancelled interrupt: <c>RUN_STARTED</c> and <c>RUN_FINISHED</c>.
    /// </summary>
    public IReadOnlyList<BaseEvent> Short(RunFinishedOutcome? outcome)
    {
        var events = new List<BaseEvent>();
        Start(events, null);
        events.Add(Finished(outcome, null));
        return events;
    }

    private void Start(List<BaseEvent> events, NhAssistantTurnStartedEvent? started)
    {
        if (_started)
        {
            return;
        }

        _started = true;
        var metadata = new JsonObject { ["conversationId"] = conversationId.ToString("D") };
        if (started is not null)
        {
            metadata["turnId"] = started.TurnId.ToString("D");
        }

        events.Add(new RunStartedEvent
        {
            ThreadId = threadId,
            RunId = runId,
            ProtocolVersion = AGUIProtocol.Version,
            Metadata = NhAssistantAGUIInterrupts.Element(metadata)
        });
    }

    private IEnumerable<BaseEvent> Text(NhAssistantMessageDeltaEvent delta)
    {
        if (string.IsNullOrEmpty(delta.Text))
        {
            return [];
        }

        var events = new List<BaseEvent>();
        if (_openText is null)
        {
            _textSegments++;
            // The first text of the turn keeps the id of the agent's message; text after a tool call is a
            // message of its own in AG-UI.
            _openText = _textSegments == 1 ? delta.MessageId.ToString("D") : $"{delta.MessageId:D}-{_textSegments}";
            events.Add(new TextMessageStartEvent { MessageId = _openText, Role = Assistant });
        }

        events.Add(new TextMessageContentEvent { MessageId = _openText, Delta = delta.Text });
        return events;
    }

    private IEnumerable<BaseEvent> CloseText()
    {
        if (_openText is null)
        {
            return [];
        }

        var end = new TextMessageEndEvent { MessageId = _openText };
        _openText = null;
        return [end];
    }

    private IEnumerable<BaseEvent> ToolStarted(NhAssistantToolStartedEvent tool)
    {
        var events = CloseText().ToList();
        if (DecidedCall == tool.InvocationId)
        {
            _reportedCalls.Add(tool.InvocationId);
            return events;
        }
        if (registration.TryGetClientTool(tool.ToolId, out _))
        {
            // A client tool becomes a frontend call with its bound arguments when the turn hands it to the
            // client; a call that is refused at once is reported as a server call when it completes.
            _deferredClientTools[tool.InvocationId] = tool;
            return events;
        }

        events.AddRange(Call(tool.InvocationId, tool.ToolId, ArgumentsOf(tool.ArgumentsPreview), tool));
        return events;
    }

    private IEnumerable<BaseEvent> ToolCompleted(NhAssistantToolCompletedEvent tool)
    {
        var events = CloseText().ToList();
        if (ReportedClientTool == tool.InvocationId)
        {
            // The client ran this call, and its tool message already is the call's result.
            _deferredClientTools.Remove(tool.InvocationId);
            return events;
        }
        if (_deferredClientTools.Remove(tool.InvocationId, out var started))
        {
            events.AddRange(Call(started.InvocationId, started.ToolId, ArgumentsOf(started.ArgumentsPreview), started));
        }

        var completed = new NhAssistantToolCompletedDto(tool.InvocationId, tool.Status, tool.ResultCode, tool.ResultPreview)
        {
            ResultPresentation = NhAssistantToolResultPresentationDto.From(tool.ResultPresentation)
        };
        events.Add(new ToolCallResultEvent
        {
            MessageId = $"{tool.InvocationId:D}-result",
            ToolCallId = tool.InvocationId.ToString("D"),
            Role = AGUIRoles.Tool,
            Content = new AGUIContent(JsonSerializer.Serialize(completed, NhAssistantJsonSerializerContext.Default.NhAssistantToolCompletedDto))
        });
        return events;
    }

    private IEnumerable<BaseEvent> ApprovalRequired(NhAssistantApprovalRequiredEvent approval)
    {
        var events = CloseText().ToList();
        _lastWait = approval;
        events.AddRange(Custom(NhAssistantAGUICustomEvents.ApprovalRequired, Part(approval.Approval)));
        return events;
    }

    private IEnumerable<BaseEvent> QuestionAsked(NhAssistantQuestionAskedEvent asked)
    {
        // The run ends with the question's interrupt, which carries the question and its choices.
        _lastWait = asked;
        return CloseText();
    }

    private IEnumerable<BaseEvent> ClientToolRequested(NhAssistantClientToolRequestedEvent requested)
    {
        var events = CloseText().ToList();
        var clientTool = requested.ClientTool;
        _deferredClientTools.Remove(clientTool.InvocationId, out var started);
        _lastWait = requested;
        var arguments = requested.Arguments is { ValueKind: JsonValueKind.Object } bound ? bound.GetRawText() : "{}";
        events.AddRange(Call(clientTool.InvocationId, clientTool.ToolId, arguments, started));
        return events;
    }

    /// <summary>
    /// The plan as the run's whole state: AG-UI replaces a client's state with every snapshot, and the plan is
    /// a snapshot too.
    /// </summary>
    private static IEnumerable<BaseEvent> PlanUpdated(NhAssistantPlanUpdatedEvent plan)
    {
        var steps = JsonSerializer.SerializeToNode(
            NhAssistantDtoMapper.ToPlanDto(plan.Plan),
            NhAssistantJsonSerializerContext.Default.NhAssistantPlanDto)!.AsObject();
        var state = new JsonObject
        {
            ["plan"] = new JsonObject
            {
                ["turnId"] = plan.TurnId.ToString("D"),
                ["messageId"] = plan.MessageId.ToString("D"),
                ["steps"] = steps["steps"]?.DeepClone(),
                ["updatedAt"] = steps["updatedAt"]?.DeepClone()
            }
        };
        return [new StateSnapshotEvent { Snapshot = NhAssistantAGUIInterrupts.Element(state) }];
    }

    private IEnumerable<BaseEvent> Completed(NhAssistantTurnCompletedEvent completed)
    {
        if (completed.Status == NhAssistantTurnStatuses.Failed)
        {
            // A turn that failed, such as one whose model was unavailable, is a failed run.
            return Failed(completed.ErrorCode ?? NhAssistantErrorCodes.TurnFailed);
        }

        var events = CloseText().ToList();
        RunFinishedOutcome? outcome = completed.Status switch
        {
            NhAssistantTurnStatuses.WaitingForApproval => _lastWait switch
            {
                NhAssistantApprovalRequiredEvent approval => Interrupt(NhAssistantAGUIInterrupts.Approval(
                    approval.Approval,
                    approval.InvocationId is { } call && _reportedCalls.Contains(call) ? call.ToString("D") : null)),
                NhAssistantQuestionAskedEvent asked => Interrupt(NhAssistantAGUIInterrupts.Question(asked.Question)),
                // A client tool waits as a frontend tool call: the client runs it and reports its result.
                _ => null
            },
            NhAssistantTurnStatuses.Cancelled => new RunFinishedCancelledOutcome(),
            _ => null
        };
        events.Add(Finished(outcome, completed));
        return events;
    }

    private IEnumerable<BaseEvent> Failed(string code)
    {
        var events = CloseText().ToList();
        _finished = true;
        events.Add(new RunErrorEvent
        {
            // Content-free: the code names what happened, and clients localize it.
            Message = "The assistant could not complete the turn.",
            Code = code
        });
        return events;
    }

    private RunFinishedEvent Finished(RunFinishedOutcome? outcome, NhAssistantTurnCompletedEvent? completed)
    {
        _finished = true;
        var finished = new RunFinishedEvent
        {
            ThreadId = threadId,
            RunId = runId,
            Outcome = outcome
        };
        if (completed is null)
        {
            return finished;
        }

        var metadata = new JsonObject
        {
            ["turnId"] = completed.TurnId.ToString("D"),
            ["status"] = completed.Status,
            ["toolCalls"] = completed.Usage.ToolCalls
        };
        if (completed.ErrorCode is not null)
        {
            metadata["errorCode"] = completed.ErrorCode;
        }
        finished.Metadata = NhAssistantAGUIInterrupts.Element(metadata);
        if (completed.Usage is { InputTokens: > 0 } or { OutputTokens: > 0 })
        {
            finished.Usage =
            [
                new TokenUsage
                {
                    InputTokens = completed.Usage.InputTokens,
                    OutputTokens = completed.Usage.OutputTokens,
                    TotalTokens = (long)completed.Usage.InputTokens + completed.Usage.OutputTokens
                }
            ];
        }
        return finished;
    }

    private IEnumerable<BaseEvent> Call(Guid invocationId, string toolId, string arguments, NhAssistantToolStartedEvent? started)
    {
        _reportedCalls.Add(invocationId);
        var callId = invocationId.ToString("D");
        var metadata = new JsonObject();
        if (started is not null)
        {
            metadata["displayName"] = started.DisplayName;
            metadata["toolVersion"] = started.ToolVersion;
        }

        return
        [
            new ToolCallStartEvent
            {
                ToolCallId = callId,
                ToolCallName = toolId,
                Metadata = started is null ? null : NhAssistantAGUIInterrupts.Element(metadata)
            },
            new ToolCallArgsEvent { ToolCallId = callId, Delta = arguments },
            new ToolCallEndEvent { ToolCallId = callId }
        ];
    }

    /// <summary>
    /// The arguments of a server call as AG-UI carries them, a JSON object: the arguments' preview when it is
    /// one, otherwise the preview as text, since a bounded or redacted preview is no JSON object.
    /// </summary>
    private static string ArgumentsOf(string? preview)
    {
        if (preview is null)
        {
            return "{}";
        }

        try
        {
            using var document = JsonDocument.Parse(preview);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                return preview;
            }
        }
        catch (JsonException)
        {
            // A bounded preview is no longer valid JSON.
        }

        return new JsonObject { ["preview"] = preview }.ToJsonString();
    }

    private static RunFinishedInterruptOutcome Interrupt(AGUIInterrupt interrupt)
    {
        return new RunFinishedInterruptOutcome { Interrupts = [interrupt] };
    }

    private static IEnumerable<BaseEvent> Custom(string name, JsonElement value)
    {
        return [new CustomEvent { Name = name, Value = value }];
    }

    private static JsonElement Part(NhAssistantPartView part)
    {
        return JsonSerializer.SerializeToElement(
            NhAssistantDtoMapper.ToDto(part),
            NhAssistantJsonSerializerContext.Default.NhAssistantMessagePartDto);
    }
}
