using NewHeap.Platform.AI.Chat.Entities;

namespace NewHeap.Platform.AI.Chat.Runtime;

/// <summary>
/// Asks the person of one governed tool call of a chat turn for what an MCP server needs while its
/// tool runs: the agent's message shows the server's request attributed to the server, the person
/// answers in the chat, and the answer goes straight back to the waiting call. The server's message is
/// never given to the model, and the answer is never stored or audited; the audit records the server,
/// the call, the mode and the outcome. Without a person (<paramref name="personActorId"/> is
/// <see langword="null"/>) it declines at once.
/// <para>
/// The request waits the server's wait time (<see cref="NhAssistantElicitationRequest.Within"/>, five
/// minutes unless the server sets one to ten) on the application's clock, and never past the turn's
/// deadline: the turn's own timeout keeps running while the person is asked, so the wait never makes a
/// turn longer than <see cref="NhAssistantLimits.TurnTimeout"/>. The tool's own timeout stands still
/// meanwhile (the MCP layer pauses it). When the wait ends first, the server reads a cancellation and
/// the audit records <c>&lt;mode&gt;-expired</c>.
/// </para>
/// </summary>
internal sealed class NhAssistantTurnElicitor(
    NhAssistantToolCallInterceptor interceptor,
    NhAssistantTurnState state,
    AssistantToolInvocation row,
    NhAssistantElicitationRegistry? registry,
    string? personActorId,
    TimeProvider applicationClock) : INhAssistantElicitor
{
    public async Task<NhAssistantElicitationOutcome> ElicitAsync(
        NhAssistantElicitationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (registry is null || personActorId is null)
        {
            await RecordDeclinedAsync(request, NhAssistantElicitationReasons.Unanswerable);
            return NhAssistantElicitationOutcome.Declined(NhAssistantElicitationReasons.Unanswerable);
        }

        var within = WaitOf(request);
        if (within <= TimeSpan.Zero)
        {
            // The turn's deadline has passed: nobody could answer in time.
            await RecordDeclinedAsync(request, NhAssistantElicitationReasons.Expired);
            return NhAssistantElicitationOutcome.Cancelled(NhAssistantElicitationReasons.Expired);
        }

        var now = applicationClock.GetUtcNow();
        var part = new NhAssistantStoredPart(
            NhAssistantStoredPart.ElicitationType,
            Text: request.Message,
            InvocationId: row.Id,
            ServerId: request.ServerId,
            DisplayName: request.ServerName,
            LinkUrl: request.Url,
            AddresseeActorId: personActorId,
            ExpiresAt: now.Add(within),
            ElicitationId: Guid.NewGuid(),
            Mode: request.Mode,
            Fields: request.Fields.Count == 0 ? null : request.Fields,
            Status: NhAssistantElicitationStatuses.Pending);

        using var waiter = registry.Register(
            part.ElicitationId!.Value,
            state.Conversation.Id,
            personActorId,
            request.Mode,
            request.Fields);
        state.AddPart(part);
        await interceptor.FlushAsync(CancellationToken.None);
        await state.EmitAsync(new NhAssistantElicitationRequestedEvent(ViewOf(part)));
        await interceptor.RecordBusinessEventAsync(
            Event(NhAssistantAuditEventKind.McpElicitationRequested, request) with { ResultCode = request.Mode },
            CancellationToken.None);

        NhAssistantElicitationOutcome outcome;
        string status;
        // The wait time follows the application's clock, as other business deadlines do.
        using (var expiry = new CancellationTokenSource(within, applicationClock))
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, expiry.Token))
        {
            try
            {
                outcome = await waiter.Answer.WaitAsync(deadline.Token);
                status = outcome.Action switch
                {
                    NhAssistantElicitationActions.Accept => NhAssistantElicitationStatuses.Accepted,
                    NhAssistantElicitationActions.Decline => NhAssistantElicitationStatuses.Declined,
                    _ => NhAssistantElicitationStatuses.Cancelled
                };
            }
            catch (OperationCanceledException)
            {
                // The wait ended, or the turn or the tool call ended while it waited; the server reads
                // a cancellation, which is all it may learn.
                var expired = !cancellationToken.IsCancellationRequested;
                outcome = NhAssistantElicitationOutcome.Cancelled(expired ? NhAssistantElicitationReasons.Expired : NhAssistantElicitationReasons.Stopped);
                status = expired ? NhAssistantElicitationStatuses.Expired : NhAssistantElicitationStatuses.Cancelled;
            }
        }

        var closed = part with
        {
            Status = status,
            ClosedAt = applicationClock.GetUtcNow()
        };
        state.ReplacePart(part, closed);
        await interceptor.FlushAsync(CancellationToken.None);
        await state.EmitAsync(new NhAssistantElicitationClosedEvent(ViewOf(closed)));
        await interceptor.RecordBusinessEventAsync(
            Event(NhAssistantAuditEventKind.McpElicitationClosed, request) with { ResultCode = request.Mode + "-" + status },
            CancellationToken.None);
        return outcome;
    }

    /// <summary>
    /// How long the request waits: the server's wait time within its bounds, and never past the turn's
    /// deadline, which the system clock measures as the turn's own timeout does.
    /// </summary>
    private TimeSpan WaitOf(NhAssistantElicitationRequest request)
    {
        var wait = NhAssistantElicitationLimits.DefaultWait;
        if (request.Within is { } asked && asked > TimeSpan.Zero)
        {
            wait = asked < NhAssistantElicitationLimits.MaxWait ? asked : NhAssistantElicitationLimits.MaxWait;
        }

        var turnLeft = state.Scope.Deadline - DateTimeOffset.UtcNow;
        return turnLeft < wait ? turnLeft : wait;
    }

    /// <summary>
    /// Audits a request that was declined without asking anyone.
    /// </summary>
    public async Task RecordDeclinedAsync(NhAssistantElicitationRequest request, string reason)
    {
        ArgumentNullException.ThrowIfNull(request);
        await interceptor.RecordBusinessEventAsync(
            Event(NhAssistantAuditEventKind.McpElicitationClosed, request) with { ResultCode = request.Mode + "-" + reason },
            CancellationToken.None);
    }

    private NhAssistantElicitationView ViewOf(NhAssistantStoredPart part)
    {
        return NhAssistantElicitationView.From(part, DateTimeOffset.MinValue)
            ?? throw new InvalidOperationException("A request for input has no view.");
    }

    private NhAssistantAuditEvent Event(NhAssistantAuditEventKind kind, NhAssistantElicitationRequest request)
    {
        return new NhAssistantAuditEvent(
            kind,
            state.Conversation.Id,
            state.Scope.TurnId,
            personActorId ?? state.Scope.OwnerActorId,
            state.Scope.Agent.Id,
            state.Scope.Agent.Version,
            DateTimeOffset.UtcNow)
        {
            ToolId = row.ToolId,
            ToolVersion = row.ToolVersion,
            InvocationId = row.Id,
            ObjectId = request.ServerId,
            StartedAt = row.StartedAt
        };
    }
}
