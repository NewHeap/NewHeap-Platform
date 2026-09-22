using System.Text.Json;
using System.Globalization;
using System.Threading.Channels;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Governance;
using NewHeap.Platform.AI.Chat.Persistence;

namespace NewHeap.Platform.AI.Chat.Runtime;

/// <summary>
/// Mutable state of one running turn. Only the turn's own asynchronous flow touches it.
/// </summary>
internal sealed class NhAssistantTurnState
{
    private readonly object _sync = new();
    private readonly List<NhAssistantStoredPart> _parts = [];

    public required NhAssistantTurnScope Scope { get; init; }

    public required AssistantConversation Conversation { get; init; }

    public required NhAssistantAgent EffectiveAgent { get; init; }

    /// <summary>
    /// Tools of the MCP servers assigned to the agent, loaded once per turn.
    /// </summary>
    public NhAssistantMcpToolSet? McpTools { get; set; }

    public required Guid UserMessageId { get; init; }

    public required Guid AssistantMessageId { get; init; }

    public required ChannelWriter<NhAssistantTurnEvent> Events { get; init; }

    public NhAssistantApprovalView? PendingApproval { get; set; }

    public bool ToolCallLimitReached { get; set; }

    /// <summary>
    /// Set when the durable daily tool budget refused a tool call in this turn.
    /// </summary>
    public bool ToolBudgetExhausted { get; set; }

    public bool InstructionsTooLong { get; set; }

    /// <summary>
    /// Set after a rejected approval and before the closing answer after a tool limit: the model may
    /// give one closing message but call no more tools.
    /// </summary>
    public bool ToolsDisabled { get; set; }

    public int InputTokens { get; set; }

    public int OutputTokens { get; set; }

    public int ToolCalls { get; set; }

    public IReadOnlyList<NhAssistantStoredPart> Parts
    {
        get
        {
            lock (_sync)
            {
                return _parts.ToArray();
            }
        }
    }

    public void AppendText(string text)
    {
        lock (_sync)
        {
            if (_parts.Count > 0 && _parts[^1].Type == NhAssistantStoredPart.TextType)
            {
                _parts[^1] = _parts[^1] with { Text = _parts[^1].Text + text };
                return;
            }
            _parts.Add(new NhAssistantStoredPart(NhAssistantStoredPart.TextType, Text: text));
        }
    }

    public void AddPart(NhAssistantStoredPart part)
    {
        lock (_sync)
        {
            _parts.Add(part);
        }
    }

    public ValueTask EmitAsync(NhAssistantTurnEvent evt)
    {
        // The stream is unbounded; a disconnected client never blocks the turn.
        Events.TryWrite(evt);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Result of one governed function execution inside a turn.
/// </summary>
internal sealed record NhAssistantToolExecution(
    object? Result,
    NhAssistantToolOutcome Outcome,
    NhAssistantToolCallScope Call);

/// <summary>
/// Agent Framework function middleware that turns every governed tool call into persisted,
/// streamed and audited assistant state. It never bypasses the governed function: the call runs
/// through <c>next</c>, which executes the <see cref="INhAiGovernedAIFunction"/> and its shared
/// <see cref="INhAiToolInvoker"/>. When the invoker reports a missing approval, the interceptor
/// creates the exact NewHeap proposal from the arguments the invoker evaluated, pauses the turn and
/// terminates the model loop.
/// </summary>
internal sealed class NhAssistantToolCallInterceptor(
    NhAssistantTurnState state,
    INhAssistantStore store,
    INhAiProposalFactory proposalFactory,
    NhAssistantToolPresentationResolver toolPresentation,
    IReadOnlyList<INhAssistantBusinessAuditSink> businessSinks,
    ILogger? logger = null)
{
    public const string ToolsDisabledCode = "assistant-tools-disabled";

    private IReadOnlyDictionary<string, NhAiToolDescriptor> _offeredTools =
        new Dictionary<string, NhAiToolDescriptor>(StringComparer.Ordinal);

    /// <summary>
    /// Registers the governed descriptors the adapter offered to the agent, keyed by function name.
    /// Agent Framework middleware may wrap the governed function; the call itself still runs through it.
    /// </summary>
    public void UseOfferedTools(IEnumerable<NhAiToolDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        var offered = new Dictionary<string, NhAiToolDescriptor>(StringComparer.Ordinal);
        foreach (var descriptor in descriptors)
        {
            if (!string.IsNullOrWhiteSpace(descriptor.ExportName) && !offered.TryAdd(descriptor.ExportName, descriptor))
            {
                throw new InvalidOperationException(
                    $"Two offered assistant tools share the function name '{descriptor.ExportName}'.");
            }
        }
        _offeredTools = offered;
    }

    private NhAiToolDescriptor? FindOffered(string functionName)
    {
        return _offeredTools.TryGetValue(functionName, out var descriptor) ? descriptor : null;
    }

    public async ValueTask<object?> InvokeAsync(
        AIAgent agent,
        FunctionInvocationContext context,
        Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var descriptor = FindGoverned(context.Function)?.Descriptor
            ?? FindOffered(context.Function.Name)
            ?? throw new InvalidOperationException(
                "The assistant only executes governed NewHeap AI functions.");

        if (state.ToolsDisabled)
        {
            context.Terminate = true;
            return Refusal(ToolsDisabledCode);
        }
        if (state.Scope.IncrementToolCalls() > state.Scope.Limits.MaxToolCallsPerTurn)
        {
            state.ToolCallLimitReached = true;
            context.Terminate = true;
            return Refusal(NhAssistantErrorCodes.ToolCallLimitReached);
        }

        var presentationCulture = CultureInfo.CurrentUICulture;
        var displayName = await toolPresentation.ResolveDisplayNameAsync(
            descriptor,
            presentationCulture,
            cancellationToken);
        var row = await BeginToolCallAsync(
            descriptor,
            context.CallContent.CallId,
            context.Function.Name,
            context.Arguments,
            displayName,
            cancellationToken);
        var execution = await ExecuteAsync(
            descriptor,
            row,
            null,
            null,
            token => next(context, token),
            cancellationToken,
            logger);
        if (execution.Outcome.Kind == NhAssistantToolOutcomeKind.ApprovalMissing
            && await RequestApprovalAsync(
                descriptor,
                row,
                execution.Call,
                presentationCulture,
                cancellationToken))
        {
            context.Terminate = true;
            return execution.Result;
        }

        await CompleteToolCallAsync(row, execution, cancellationToken);
        if (state.Scope.BudgetExhausted)
        {
            // A reservation inside a tool call is a tool-budget reservation.
            state.ToolBudgetExhausted = true;
            context.Terminate = true;
        }
        return execution.Result;
    }

    /// <summary>
    /// Executes a governed function inside an assistant tool-call scope and classifies its outcome
    /// from the content-free audit record the shared invoker wrote.
    /// </summary>
    public static async Task<NhAssistantToolExecution> ExecuteAsync(
        NhAiToolDescriptor descriptor,
        AssistantToolInvocation row,
        Guid? proposalId,
        Guid? approvalId,
        Func<CancellationToken, ValueTask<object?>> invoke,
        CancellationToken cancellationToken,
        ILogger? logger = null)
    {
        var call = new NhAssistantToolCallScope
        {
            InvocationId = row.Id,
            Descriptor = descriptor,
            IdempotencyKey = "assistant-" + (proposalId ?? row.Id).ToString("N"),
            ProposalId = proposalId,
            ApprovalId = approvalId
        };
        object? result;
        using (NhAssistantExecutionScope.EnterCall(call))
        {
            try
            {
                result = await invoke(cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
                // Transport or remote failures end this tool call only; the model receives a stable code.
                // The log is content-free: tool identity, turn and exception type, never arguments,
                // results, exception messages or stack traces.
                if (logger is not null)
                {
                    LogUnexpectedToolException(
                        logger,
                        descriptor.Id,
                        descriptor.Version,
                        NhAssistantExecutionScope.CurrentTurn?.TurnId,
                        exception.GetType().FullName ?? exception.GetType().Name);
                }
                var code = descriptor.Id.StartsWith("mcp.", StringComparison.Ordinal)
                    ? NhAssistantAdminErrorCodes.McpUnreachable
                    : NhAiToolFailureCodes.Failed;
                return new NhAssistantToolExecution(
                    Refusal(code),
                    new NhAssistantToolOutcome(NhAssistantToolOutcomeKind.Failed, code),
                    call);
            }
        }

        var records = call.AuditRecords;
        var outcome = records.Count > 0
            ? NhAssistantToolOutcome.FromAudit(records[^1])
            : ClassifyResult(result);
        return new NhAssistantToolExecution(result, outcome, call);
    }

    public async Task<AssistantToolInvocation> BeginToolCallAsync(
        NhAiToolDescriptor descriptor,
        string callId,
        string functionName,
        IEnumerable<KeyValuePair<string, object?>>? arguments,
        string displayName,
        CancellationToken cancellationToken)
    {
        var row = new AssistantToolInvocation
        {
            Id = Guid.NewGuid(),
            ConversationId = state.Conversation.Id,
            TurnId = state.Scope.TurnId,
            MessageId = state.AssistantMessageId,
            CallId = NhAssistantContent.Bound(string.IsNullOrWhiteSpace(callId) ? Guid.NewGuid().ToString("N") : callId, 128),
            FunctionName = NhAssistantContent.Bound(functionName, 256),
            ToolId = descriptor.Id,
            ToolVersion = descriptor.Version,
            ContractHash = NhAssistantContent.Bound(descriptor.ContractHash, 128),
            DisplayName = displayName,
            Status = NhAssistantToolCallStatuses.Running,
            ArgumentsJson = NhAssistantContent.SerializeArguments(arguments),
            DataClassification = descriptor.DataClassification,
            RetentionCategory = descriptor.RetentionCategory,
            StartedAt = DateTimeOffset.UtcNow
        };
        await store.AddToolInvocationAsync(row, cancellationToken);
        state.ToolCalls++;
        state.AddPart(new NhAssistantStoredPart(NhAssistantStoredPart.ToolCallType, InvocationId: row.Id));
        await FlushAsync(cancellationToken);
        await state.EmitAsync(new NhAssistantToolStartedEvent(
            row.Id,
            row.ToolId,
            row.ToolVersion,
            row.DisplayName,
            NhAssistantContent.Preview(row.ArgumentsJson, row.DataClassification)));
        return row;
    }

    public async Task CompleteToolCallAsync(
        AssistantToolInvocation row,
        NhAssistantToolExecution execution,
        CancellationToken cancellationToken)
    {
        var succeeded = execution.Outcome.Kind == NhAssistantToolOutcomeKind.Succeeded;
        row.Status = succeeded ? NhAssistantToolCallStatuses.Succeeded : NhAssistantToolCallStatuses.Failed;
        row.ResultCode = succeeded ? null : NhAssistantContent.Bound(execution.Outcome.Code, 128);
        row.ResultJson = NhAssistantContent.SerializeResult(execution.Result);
        row.CompletedAt = DateTimeOffset.UtcNow;
        await store.UpdateToolInvocationAsync(row, cancellationToken);
        await state.EmitAsync(new NhAssistantToolCompletedEvent(
            row.Id,
            row.Status,
            row.ResultCode,
            NhAssistantContent.ResultPreview(row.ResultJson, row.DataClassification)));
    }

    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        await store.UpdateMessageAsync(
            state.AssistantMessageId,
            NhAssistantContent.SerializeParts(state.Parts),
            state.InputTokens,
            state.OutputTokens,
            state.ToolCalls,
            cancellationToken);
    }

    public async Task RecordBusinessEventAsync(NhAssistantAuditEvent evt, CancellationToken cancellationToken)
    {
        foreach (var sink in businessSinks)
        {
            await sink.RecordAsync(state.Scope.WithPromptIdentity(evt), cancellationToken);
        }
    }

    private async Task<bool> RequestApprovalAsync(
        NhAiToolDescriptor descriptor,
        AssistantToolInvocation row,
        NhAssistantToolCallScope call,
        CultureInfo culture,
        CancellationToken cancellationToken)
    {
        if (call.CapturedArguments is null
            || call.CapturedContext is not { RunId: not null, AccountableOwnerId: not null } context)
        {
            // Another evidence provider answered; the assistant cannot bind a proposal to this call.
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.Add(state.Scope.Limits.ApprovalLifetime);
        var targets = context.ExecutionScopes
            .Take(32)
            .Select(scope => new NhAiProposalTarget(scope.Type, scope.Id))
            .ToArray();
        var intent = NhAssistantContent.Bound(
            string.IsNullOrWhiteSpace(descriptor.Description) ? descriptor.Id : descriptor.Description,
            512);
        var proposal = proposalFactory.Create(new NhAiProposalCreateRequest(
            Guid.NewGuid(),
            context.RunId,
            context.ActorKind,
            context.ActorId,
            context.AccountableOwnerId,
            descriptor,
            call.CapturedArguments,
            targets,
            intent,
            [EffectCode(descriptor.Effect)],
            new Dictionary<string, string>(StringComparer.Ordinal),
            new NhAiActionBudget(1),
            now,
            expiresAt)
        {
            ModelProfileName = context.ModelProfileName,
            PromptVersion = context.PromptVersion,
            PromptHash = context.PromptHash,
            CatalogHash = context.CatalogHash,
            ContextHash = context.ContextHash
        });
        var targetLabels = proposal.Targets
            .Select(target => $"{target.Type}:{target.Id}")
            .ToArray();
        var presentation = await toolPresentation.ResolveApprovalAsync(
            descriptor,
            call.CapturedArguments,
            context,
            culture,
            cancellationToken);
        if (presentation is not null)
        {
            row.DisplayName = presentation.ToolDisplayName;
        }
        var approval = new AssistantApproval
        {
            Id = Guid.NewGuid(),
            ConversationId = state.Conversation.Id,
            TurnId = state.Scope.TurnId,
            ToolInvocationId = row.Id,
            ProposalId = proposal.ProposalId,
            ProposalHash = proposal.ProposalHash,
            ProposalJson = NhAssistantProposalSerializer.Serialize(proposal),
            ToolId = descriptor.Id,
            Summary = "Approval is required before this tool can run.",
            PresentationJson = NhAssistantToolPresentationResolver.Serialize(presentation),
            ArgumentsPreview = NhAssistantContent.Preview(row.ArgumentsJson, row.DataClassification) ?? "{}",
            TargetsJson = JsonSerializer.Serialize(targetLabels, NhAssistantContent.JsonOptions),
            Status = NhAssistantApprovalStatuses.Pending,
            CreatedAt = now,
            ExpiresAt = expiresAt,
            ConcurrencyStamp = Guid.NewGuid()
        };
        await store.AddApprovalAsync(approval, cancellationToken);

        row.Status = NhAssistantToolCallStatuses.AwaitingApproval;
        row.ProposalId = proposal.ProposalId;
        row.ApprovalId = approval.Id;
        await store.UpdateToolInvocationAsync(row, cancellationToken);

        var view = new NhAssistantApprovalView(
            approval.Id,
            approval.ProposalId,
            approval.ProposalHash,
            approval.ToolId,
            approval.Summary,
            presentation,
            approval.ArgumentsPreview,
            targetLabels,
            approval.ExpiresAt,
            approval.Status);
        state.PendingApproval = view;
        state.AddPart(new NhAssistantStoredPart(NhAssistantStoredPart.ApprovalType, ApprovalId: approval.Id));
        await FlushAsync(cancellationToken);
        await state.EmitAsync(new NhAssistantApprovalRequiredEvent(view));
        await RecordBusinessEventAsync(
            new NhAssistantAuditEvent(
                NhAssistantAuditEventKind.ApprovalRequested,
                state.Conversation.Id,
                state.Scope.TurnId,
                state.Scope.OwnerActorId,
                state.Scope.Agent.Id,
                state.Scope.Agent.Version,
                now)
            {
                ToolId = descriptor.Id,
                ToolVersion = descriptor.Version,
                InvocationId = row.Id,
                ResultCode = NhAiToolFailureCodes.ApprovalRequired,
                ApprovalId = approval.Id,
                ProposalId = proposal.ProposalId,
                StartedAt = row.StartedAt
            },
            cancellationToken);
        return true;
    }

    /// <summary>
    /// Finds the governed function behind Agent Framework middleware wrappers.
    /// </summary>
    private static INhAiGovernedAIFunction? FindGoverned(AIFunction function)
    {
        AIFunction? current = function;
        for (var depth = 0; current is not null && depth < 8; depth++)
        {
            if (current is INhAiGovernedAIFunction governed)
            {
                return governed;
            }
            if (current.GetService(typeof(INhAiGovernedAIFunction)) is INhAiGovernedAIFunction service)
            {
                return service;
            }
            current = null;
        }
        return null;
    }

    private static NhAssistantToolOutcome ClassifyResult(object? result)
    {
        if (result is JsonElement { ValueKind: JsonValueKind.Object } element
            && element.TryGetProperty("success", out var success)
            && success.ValueKind == JsonValueKind.True)
        {
            return new NhAssistantToolOutcome(NhAssistantToolOutcomeKind.Succeeded, "succeeded");
        }
        return new NhAssistantToolOutcome(NhAssistantToolOutcomeKind.Failed, NhAiToolFailureCodes.Failed);
    }

    private static JsonElement Refusal(string code)
    {
        return JsonSerializer.SerializeToElement(
            new Dictionary<string, object?>
            {
                ["success"] = false,
                ["code"] = code
            },
            NhAssistantContent.JsonOptions);
    }

    private static readonly Action<ILogger, string, int, Guid?, string, Exception?> UnexpectedToolException =
        LoggerMessage.Define<string, int, Guid?, string>(
            LogLevel.Warning,
            new EventId(1, "AssistantToolUnexpectedException"),
            "Assistant tool {ToolId} v{ToolVersion} failed unexpectedly in turn {TurnId} with {ExceptionType}.");

    private static void LogUnexpectedToolException(
        ILogger logger,
        string toolId,
        int toolVersion,
        Guid? turnId,
        string exceptionType)
    {
        UnexpectedToolException(logger, toolId, toolVersion, turnId, exceptionType, null);
    }

    private static string EffectCode(NhAiToolEffect effect)
    {
        return effect switch
        {
            NhAiToolEffect.ReadOnly => "read-only",
            NhAiToolEffect.IdempotentMutation => "idempotent-mutation",
            NhAiToolEffect.Mutation => "mutation",
            NhAiToolEffect.ExternalSideEffect => "external-side-effect",
            NhAiToolEffect.Destructive => "destructive",
            _ => "unknown"
        };
    }
}
