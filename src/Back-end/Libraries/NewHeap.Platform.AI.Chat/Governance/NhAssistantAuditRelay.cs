using NewHeap.Platform.AI.Chat.Runtime;

namespace NewHeap.Platform.AI.Chat.Governance;

/// <summary>
/// NewHeap audit sink that observes governed tool outcomes inside an assistant tool call. It records
/// the content-free audit record on the ambient call, so the turn runner can classify the outcome,
/// and relays a content-free <see cref="NhAssistantAuditEvent"/> to the consumer's business sinks.
/// Records outside assistant turns are ignored.
/// </summary>
internal sealed class NhAssistantAuditRelay(
    IEnumerable<INhAssistantBusinessAuditSink> businessSinks) : INhAiAuditSink
{
    private readonly IReadOnlyList<INhAssistantBusinessAuditSink> _businessSinks = businessSinks.ToArray();

    public async ValueTask WriteAsync(
        NhAiAuditRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var turn = NhAssistantExecutionScope.CurrentTurn;
        var call = NhAssistantExecutionScope.CurrentCall;
        if (turn is null || call is null)
        {
            return;
        }

        call.RecordAudit(record);
        var outcome = NhAssistantToolOutcome.FromAudit(record);
        if (outcome.Kind == NhAssistantToolOutcomeKind.ApprovalMissing)
        {
            // The runner reports the proposal as ApprovalRequested once it is stored.
            return;
        }

        var evt = new NhAssistantAuditEvent(
            NhAssistantAuditEventKind.ToolInvoked,
            turn.ConversationId,
            turn.TurnId,
            turn.OwnerActorId,
            turn.Agent.Id,
            turn.Agent.Version,
            record.Timestamp)
        {
            ToolId = record.ToolId,
            ToolVersion = record.ToolVersion,
            InvocationId = record.InvocationId,
            ResultCode = outcome.Code,
            ApprovalId = call.ApprovalId,
            ProposalId = call.ProposalId,
            CompletedAt = record.Timestamp,
            ConsultationId = turn.Consultation?.ConsultationId,
            ConsultationDepth = turn.Consultation?.Depth,
            AskingAgentId = turn.Consultation?.AskingAgentId,
            McpConnectionOwner = McpConnectionOwnerOf(turn, record.ToolId)
        };
        foreach (var sink in _businessSinks)
        {
            await sink.RecordAsync(turn.WithPromptIdentity(evt), cancellationToken);
        }
    }

    /// <summary>
    /// Whose credential an <c>mcp.&lt;server&gt;.&lt;tool&gt;</c> call carried; null for other tools.
    /// </summary>
    private static string? McpConnectionOwnerOf(NhAssistantTurnScope turn, string toolId)
    {
        var parts = toolId.Split('.', 3);
        if (parts.Length < 3 || parts[0] != "mcp")
        {
            return null;
        }
        return turn.McpConnectionOwners is { } owners && owners.TryGetValue(parts[1], out var owner)
            ? owner
            : NhAssistantMcpConnectionOwners.None;
    }
}

internal enum NhAssistantToolOutcomeKind
{
    Succeeded = 0,
    Failed = 1,
    ApprovalMissing = 2
}

/// <summary>
/// Content-free classification of a governed tool outcome. A failure that the audit records with its
/// own result code, such as a refusal of the invocation gate or a missing capability, keeps that code;
/// otherwise the outcome kind names it.
/// </summary>
internal sealed record NhAssistantToolOutcome(NhAssistantToolOutcomeKind Kind, string Code)
{
    public static NhAssistantToolOutcome FromAudit(NhAiAuditRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Outcome == NhAiOutcomeKind.Succeeded)
        {
            return new(NhAssistantToolOutcomeKind.Succeeded, "succeeded");
        }
        if (record.Outcome == NhAiOutcomeKind.ApprovalRequired && record.ApprovalCode is null)
        {
            // A missing approval becomes the proposal; it is no failure of its own.
            return new(NhAssistantToolOutcomeKind.ApprovalMissing, NhAiToolFailureCodes.ApprovalRequired);
        }
        if (!string.IsNullOrWhiteSpace(record.ResultCode))
        {
            return new(NhAssistantToolOutcomeKind.Failed, record.ResultCode);
        }

        return record.Outcome switch
        {
            NhAiOutcomeKind.ApprovalRequired =>
                new(NhAssistantToolOutcomeKind.Failed, NhAiToolFailureCodes.ApprovalInvalid),
            NhAiOutcomeKind.BudgetExhausted =>
                new(NhAssistantToolOutcomeKind.Failed, NhAiToolFailureCodes.BudgetDenied),
            NhAiOutcomeKind.AuthorizationDenied =>
                new(NhAssistantToolOutcomeKind.Failed, record.ApprovalCode is not null
                    ? NhAiToolFailureCodes.ExecutionEvidenceInvalid
                    : NhAiToolFailureCodes.AuthorizationDenied),
            NhAiOutcomeKind.Conflict =>
                new(NhAssistantToolOutcomeKind.Failed, record.IdempotencyCode ?? NhAiToolFailureCodes.ConcurrencyLimited),
            _ => new(
                NhAssistantToolOutcomeKind.Failed,
                record.VerificationCode is not null ? NhAiToolFailureCodes.VerificationFailed : NhAiToolFailureCodes.Failed)
        };
    }
}
