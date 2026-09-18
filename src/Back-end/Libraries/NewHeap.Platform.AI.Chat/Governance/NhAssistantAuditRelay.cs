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
            CompletedAt = record.Timestamp
        };
        foreach (var sink in _businessSinks)
        {
            await sink.RecordAsync(evt, cancellationToken);
        }
    }
}

internal enum NhAssistantToolOutcomeKind
{
    Succeeded = 0,
    Failed = 1,
    ApprovalMissing = 2
}

/// <summary>
/// Content-free classification of a governed tool outcome.
/// </summary>
internal sealed record NhAssistantToolOutcome(NhAssistantToolOutcomeKind Kind, string Code)
{
    public static NhAssistantToolOutcome FromAudit(NhAiAuditRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return record.Outcome switch
        {
            NhAiOutcomeKind.Succeeded => new(NhAssistantToolOutcomeKind.Succeeded, "succeeded"),
            NhAiOutcomeKind.ApprovalRequired when record.ApprovalCode is null =>
                new(NhAssistantToolOutcomeKind.ApprovalMissing, NhAiToolFailureCodes.ApprovalRequired),
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
                record.ResultCode
                    ?? (record.VerificationCode is not null ? NhAiToolFailureCodes.VerificationFailed : NhAiToolFailureCodes.Failed))
        };
    }
}
