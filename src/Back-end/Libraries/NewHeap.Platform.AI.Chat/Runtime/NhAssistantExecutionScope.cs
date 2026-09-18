namespace NewHeap.Platform.AI.Chat.Runtime;

/// <summary>
/// Ambient state of the assistant turn that runs in the current asynchronous flow. The turn
/// runner owns it; the invocation-gate decorator, the durable managers, the evidence provider
/// and the audit relay read it. It never carries prompt, argument or result content outside
/// the tool call that captured it.
/// </summary>
internal sealed class NhAssistantTurnScope
{
    private int _toolCalls;

    public required Guid ConversationId { get; init; }

    public required Guid TurnId { get; init; }

    public required NhAssistantAgentDefinition Agent { get; init; }

    /// <summary>
    /// The accountable human actor who owns the conversation.
    /// </summary>
    public required string OwnerActorId { get; init; }

    /// <summary>
    /// The accountable owner taken from the caller's invocation context, or the owner actor when the
    /// context resolver does not set one. Proposals and tool contexts use this value.
    /// </summary>
    public required string AccountableOwnerId { get; init; }

    public required string ModelProfileName { get; init; }

    public required string PromptVersion { get; init; }

    public required string PromptHash { get; init; }

    public required DateTimeOffset Deadline { get; init; }

    public required NhAssistantLimits Limits { get; init; }

    /// <summary>
    /// The composed instructions of the turn and their content-free identity.
    /// </summary>
    public NhAssistantComposedPrompt? Prompt { get; init; }

    /// <summary>
    /// Adds the content-free prompt identity of this turn to an audit event.
    /// </summary>
    public NhAssistantAuditEvent WithPromptIdentity(NhAssistantAuditEvent evt)
    {
        return Prompt is null
            ? evt
            : evt with
            {
                ApplicationContextVersion = Prompt.ApplicationContextVersion,
                ApplicationContextHash = Prompt.ApplicationContextHash,
                InstructionsVersion = Prompt.InstructionsVersion,
                InstructionsHash = Prompt.InstructionsHash,
                PreferencesHash = Prompt.PreferencesHash
            };
    }

    public string? TenantId { get; init; }

    public int ToolCalls => Volatile.Read(ref _toolCalls);

    /// <summary>
    /// Set when the durable daily budget of the accountable actor refused a reservation in this turn.
    /// </summary>
    public bool BudgetExhausted { get; private set; }

    public void MarkBudgetExhausted()
    {
        BudgetExhausted = true;
    }

    public int IncrementToolCalls()
    {
        return Interlocked.Increment(ref _toolCalls);
    }
}

/// <summary>
/// Ambient state of one governed tool call inside a turn.
/// </summary>
internal sealed class NhAssistantToolCallScope
{
    private readonly object _sync = new();
    private readonly List<NhAiAuditRecord> _auditRecords = [];

    public required Guid InvocationId { get; init; }

    public required NhAiToolDescriptor Descriptor { get; init; }

    public required string IdempotencyKey { get; init; }

    public Guid? ProposalId { get; init; }

    public Guid? ApprovalId { get; init; }

    /// <summary>
    /// The exact typed arguments the invoker evaluated when it asked for approval evidence.
    /// </summary>
    public object? CapturedArguments { get; private set; }

    /// <summary>
    /// The invocation context the invoker evaluated when it asked for approval evidence.
    /// </summary>
    public NhAiInvocationContext? CapturedContext { get; private set; }

    public IReadOnlyList<NhAiAuditRecord> AuditRecords
    {
        get
        {
            lock (_sync)
            {
                return _auditRecords.ToArray();
            }
        }
    }

    public void CaptureApprovalRequest(object arguments, NhAiInvocationContext context)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(context);
        CapturedArguments = arguments;
        CapturedContext = context;
    }

    public void RecordAudit(NhAiAuditRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_sync)
        {
            _auditRecords.Add(record);
        }
    }
}

internal static class NhAssistantExecutionScope
{
    private static readonly AsyncLocal<NhAssistantTurnScope?> CurrentTurnValue = new();
    private static readonly AsyncLocal<NhAssistantToolCallScope?> CurrentCallValue = new();

    public static NhAssistantTurnScope? CurrentTurn => CurrentTurnValue.Value;

    public static NhAssistantToolCallScope? CurrentCall => CurrentCallValue.Value;

    /// <summary>
    /// Enters a turn for the calling asynchronous flow and everything it awaits.
    /// </summary>
    public static IDisposable EnterTurn(NhAssistantTurnScope turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        var previousTurn = CurrentTurnValue.Value;
        var previousCall = CurrentCallValue.Value;
        CurrentTurnValue.Value = turn;
        CurrentCallValue.Value = null;
        return new Restore(() =>
        {
            CurrentTurnValue.Value = previousTurn;
            CurrentCallValue.Value = previousCall;
        });
    }

    /// <summary>
    /// Enters one governed tool call inside the current turn.
    /// </summary>
    public static IDisposable EnterCall(NhAssistantToolCallScope call)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (CurrentTurnValue.Value is null)
        {
            throw new InvalidOperationException("An assistant tool call requires an active assistant turn.");
        }
        var previous = CurrentCallValue.Value;
        CurrentCallValue.Value = call;
        return new Restore(() => CurrentCallValue.Value = previous);
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                restore();
            }
        }
    }
}
