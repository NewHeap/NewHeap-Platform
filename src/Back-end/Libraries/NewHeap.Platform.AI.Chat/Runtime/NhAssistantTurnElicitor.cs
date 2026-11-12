using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;

namespace NewHeap.Platform.AI.Chat.Runtime;

/// <summary>
/// Asks the person of one governed tool call of a chat turn for what an MCP server needs while its
/// tool runs: the agent's message shows the server's request attributed to the server, the person
/// answers in the chat, and the answer goes straight back to the waiting call. The server's message is
/// never given to the model, and the answer is never stored or audited; the audit records the server,
/// the call, the mode and the outcome. Without a person (<paramref name="personActorId"/> is
/// <see langword="null"/>) it declines at once.
/// <para>
/// While the person is asked, the turn stands still: its own timeout and the tool's stand still, the
/// turn's deadline moves later by that time (<see cref="NhAssistantTurnScope.Pause"/>), and the call lets
/// its concurrency slot go, so another person's call of the same tool can run meanwhile. A heartbeat
/// keeps the turn's claim on the conversation (<see cref="NhAssistantTurnHeartbeat"/>). The request waits
/// the server's wait time (<see cref="NhAssistantElicitationRequest.Within"/>, five minutes unless the
/// server sets one to ten) on the application's clock, and no longer than what is left of the turn's
/// <see cref="NhAssistantElicitationLimits.MaxTurnPause"/>; its <c>ExpiresAt</c> is that end. When the
/// wait ends first, the server reads a cancellation and the audit records <c>&lt;mode&gt;-expired</c>.
/// The call's caller ending it, such as the person's stop, ends the wait at once. Once the person
/// answered, the call takes a slot again within the time it has left before the answer goes back; when
/// none becomes free, the call ends as <see cref="NhAiToolFailureCodes.ConcurrencyLimited"/> and the
/// server never receives the answer.
/// </para>
/// </summary>
internal sealed class NhAssistantTurnElicitor(
    NhAssistantToolCallInterceptor interceptor,
    NhAssistantTurnState state,
    AssistantToolInvocation row,
    NhAssistantElicitationRegistry? registry,
    string? personActorId,
    TimeProvider applicationClock,
    INhAssistantStore store,
    NhAssistantTiming timing,
    ILogger? logger) : INhAssistantElicitor
{
    public async Task<NhAssistantElicitationOutcome> ElicitAsync(
        NhAssistantElicitationRequest request,
        NhAiToolCallTimeout? call,
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
            // The turn already stood still for its person as long as it may: nobody could answer in time.
            await RecordDeclinedAsync(request, NhAssistantElicitationReasons.Expired);
            return NhAssistantElicitationOutcome.Cancelled(NhAssistantElicitationReasons.Expired);
        }

        // The person's time stands still for the call, which lets its slot go, and for the turn, which
        // keeps its claim on the conversation; it begins before the person sees the request.
        var callWait = call is null ? null : await call.WaitForPersonAsync();
        try
        {
            NhAssistantElicitationOutcome outcome;
            await using (await TurnWait.BeginAsync(state.Scope, store, timing, logger))
            {
                outcome = await AskAsync(request, within, call, cancellationToken);
            }

            if (callWait is not null && !await callWait.ResumeAsync())
            {
                // Another call of the tool holds its slot: the call has ended, and the server learns
                // nothing of the answer.
                return new NhAssistantElicitationOutcome(NhAssistantElicitationActions.Cancel);
            }

            return outcome;
        }
        finally
        {
            if (callWait is not null)
            {
                await callWait.DisposeAsync();
            }
        }
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

    /// <summary>
    /// Shows the request, waits for the person within <paramref name="within"/> and closes it.
    /// </summary>
    private async Task<NhAssistantElicitationOutcome> AskAsync(
        NhAssistantElicitationRequest request,
        TimeSpan within,
        NhAiToolCallTimeout? call,
        CancellationToken cancellationToken)
    {
        // The wait time follows the application's clock, as other business deadlines do, and starts
        // with the request, so it ends exactly when the request shows it ends.
        var now = applicationClock.GetUtcNow();
        using var expiry = new CancellationTokenSource(within, applicationClock);
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

        using var waiter = registry!.Register(
            part.ElicitationId!.Value,
            state.Conversation.Id,
            personActorId!,
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
        // While the call stands still, its own token ends only when its caller ends it, such as the
        // person's stop or the end of a request-bound turn's request.
        var stopped = call?.Token ?? CancellationToken.None;
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, expiry.Token, stopped))
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
                var expired = !cancellationToken.IsCancellationRequested && !stopped.IsCancellationRequested;
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
    /// How long the request waits: the server's wait time within its bounds, and no longer than what is
    /// left of the time the turn may stand still for its person.
    /// </summary>
    private TimeSpan WaitOf(NhAssistantElicitationRequest request)
    {
        var wait = NhAssistantElicitationLimits.DefaultWait;
        if (request.Within is { } asked && asked > TimeSpan.Zero)
        {
            wait = asked < NhAssistantElicitationLimits.MaxWait ? asked : NhAssistantElicitationLimits.MaxWait;
        }

        var pauseLeft = state.Scope.PauseLeft;
        return pauseLeft < wait ? pauseLeft : wait;
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

    /// <summary>
    /// The turn standing still for its person: its pause and the heartbeat that keeps its claim on the
    /// conversation, which end together.
    /// </summary>
    private sealed class TurnWait(IDisposable pausing, NhAssistantTurnHeartbeat heartbeat) : IAsyncDisposable
    {
        public static async Task<TurnWait> BeginAsync(
            NhAssistantTurnScope scope,
            INhAssistantStore store,
            NhAssistantTiming timing,
            ILogger? logger)
        {
            var pausing = scope.Pause.Pause();
            try
            {
                return new TurnWait(pausing, await NhAssistantTurnHeartbeat.StartAsync(store, scope, timing, logger));
            }
            catch
            {
                pausing.Dispose();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            // The turn runs on before the last beat, so that beat covers all the time it has left.
            pausing.Dispose();
            await heartbeat.DisposeAsync();
        }
    }
}
