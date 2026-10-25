using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.Runtime;

namespace NewHeap.Platform.AI.Chat.AspNet;

/// <summary>
/// Writes a turn as server-sent events: <c>event: &lt;name&gt;</c>, <c>data: &lt;json&gt;</c> and an
/// empty line per event, a <c>: keep-alive</c> comment every 15 seconds, and completion after
/// <c>turn.completed</c> or <c>error</c>. A client disconnect cancels the turn through the request
/// token; the runner then releases the conversation consistently. A turn that keeps running after the
/// disconnect (<see cref="NhAssistantOptions.KeepTurnsAfterDisconnect"/>) did not take that token: its
/// remaining events are read and dropped, and the request ends with the turn, so the request's services
/// stay available to it while its tools authorize the person captured when it started.
/// </summary>
internal sealed class NhAssistantServerSentEventsResult(NhAssistantTurnHandle handle) : IResult
{
    private static readonly byte[] KeepAlive = Encoding.UTF8.GetBytes(": keep-alive\n\n");

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        var timing = httpContext.RequestServices.GetService<NhAssistantServerSentEventsTiming>()
            ?? NhAssistantServerSentEventsTiming.Default;
        var response = httpContext.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";
        httpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        var aborted = httpContext.RequestAborted;
        try
        {
            await response.Body.FlushAsync(aborted);
            while (true)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(aborted);
                wait.CancelAfter(timing.KeepAliveInterval);
                bool available;
                try
                {
                    available = await handle.Events.WaitToReadAsync(wait.Token);
                }
                catch (OperationCanceledException) when (!aborted.IsCancellationRequested)
                {
                    await response.Body.WriteAsync(KeepAlive, aborted);
                    await response.Body.FlushAsync(aborted);
                    continue;
                }
                if (!available)
                {
                    break;
                }
                while (handle.Events.TryRead(out var evt))
                {
                    await WriteEventAsync(response, evt, aborted);
                }
                await response.Body.FlushAsync(aborted);
            }
        }
        catch (OperationCanceledException) when (aborted.IsCancellationRequested)
        {
            // The client disconnected; the turn observes the same token and ends cancelled, unless it
            // keeps running after a disconnect.
            await DropRemainingAsync(handle);
        }
        catch (IOException) when (aborted.IsCancellationRequested)
        {
            // The connection closed while an event was being written.
            await DropRemainingAsync(handle);
        }

        await handle.Completion;
    }

    /// <summary>
    /// Reads the events the turn still writes after the client left and drops them, so they do not
    /// collect while the turn runs on; the conversation's snapshot and live updates carry its result.
    /// </summary>
    private static async Task DropRemainingAsync(NhAssistantTurnHandle handle)
    {
        while (await handle.Events.WaitToReadAsync(CancellationToken.None))
        {
            while (handle.Events.TryRead(out _))
            {
                // Dropped: nobody reads the stream any more.
            }
        }
    }

    private static async Task WriteEventAsync(
        HttpResponse response,
        NhAssistantTurnEvent evt,
        CancellationToken cancellationToken)
    {
        var (name, data) = Serialize(evt);
        var frame = $"event: {name}\ndata: {data}\n\n";
        await response.Body.WriteAsync(Encoding.UTF8.GetBytes(frame), cancellationToken);
    }

    internal static (string Name, string Data) Serialize(NhAssistantTurnEvent evt)
    {
        var context = NhAssistantJsonSerializerContext.Default;
        return evt switch
        {
            NhAssistantTurnStartedEvent started => ("turn.started", Json(
                new NhAssistantTurnStartedDto(started.TurnId, started.UserMessageId, started.AssistantMessageId),
                context.NhAssistantTurnStartedDto)),
            NhAssistantMessageDeltaEvent delta => ("message.delta", Json(
                new NhAssistantMessageDeltaDto(delta.MessageId, delta.Text),
                context.NhAssistantMessageDeltaDto)),
            NhAssistantToolStartedEvent tool => ("tool.started", Json(
                new NhAssistantToolStartedDto(tool.InvocationId, tool.ToolId, tool.ToolVersion, tool.DisplayName, tool.ArgumentsPreview),
                context.NhAssistantToolStartedDto)),
            NhAssistantToolCompletedEvent tool => ("tool.completed", Json(
                new NhAssistantToolCompletedDto(tool.InvocationId, tool.Status, tool.ResultCode, tool.ResultPreview),
                context.NhAssistantToolCompletedDto)),
            NhAssistantApprovalRequiredEvent approval => ("approval.required", Json(
                NhAssistantDtoMapper.ToDto(approval.Approval),
                context.NhAssistantMessagePartDto)),
            NhAssistantQuestionAskedEvent asked => ("question.asked", Json(
                NhAssistantDtoMapper.ToDto(asked.Question),
                context.NhAssistantMessagePartDto)),
            NhAssistantQuestionAnsweredEvent answered => ("question.answered", Json(
                NhAssistantDtoMapper.ToDto(answered.Question),
                context.NhAssistantMessagePartDto)),
            NhAssistantConnectionRequiredEvent connection => ("connection.required", Json(
                new NhAssistantConnectionRequiredDto(connection.ServerId, connection.DisplayName, connection.Reason),
                context.NhAssistantConnectionRequiredDto)),
            NhAssistantTurnCompletedEvent completed => ("turn.completed", Json(
                new NhAssistantTurnCompletedDto(
                    completed.TurnId,
                    completed.Status,
                    new NhAssistantUsageDto(completed.Usage.InputTokens, completed.Usage.OutputTokens, completed.Usage.ToolCalls),
                    completed.ErrorCode),
                context.NhAssistantTurnCompletedDto)),
            NhAssistantErrorEvent error => ("error", Json(
                new NhAssistantErrorDto(error.Code, error.MessageKey),
                context.NhAssistantErrorDto)),
            _ => throw new InvalidOperationException($"Unknown assistant turn event '{evt.GetType().Name}'.")
        };
    }

    private static string Json<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        return JsonSerializer.Serialize(value, typeInfo);
    }
}

/// <summary>
/// Timing of the server-sent event stream of one host. The library registers none and uses
/// <see cref="Default"/>; a test registers its own instance to shorten the keep-alive interval of its
/// host without affecting other hosts in the same process.
/// </summary>
internal sealed class NhAssistantServerSentEventsTiming
{
    internal static NhAssistantServerSentEventsTiming Default { get; } = new();

    /// <summary>
    /// How long the stream may stay silent before a <c>: keep-alive</c> comment is written.
    /// </summary>
    internal TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(15);
}

internal static class NhAssistantDtoMapper
{
    public static NhAssistantAgentSummaryDto ToDto(NhAssistantAgentDefinition agent)
    {
        return new NhAssistantAgentSummaryDto(
            agent.Id,
            agent.Version,
            agent.DisplayNameKey,
            agent.DescriptionKey,
            agent.CanMutate);
    }

    /// <summary>
    /// The agent with what people may attach to a message to it, read from <paramref name="uploads"/>.
    /// </summary>
    public static NhAssistantAgentSummaryDto ToDto(NhAssistantAgentDefinition agent, NhAssistantUploads uploads)
    {
        var rules = uploads.RulesOf(agent.Id);
        if (rules is null)
        {
            return ToDto(agent);
        }

        // The client derives a file's type with the same mappings as the server.
        var mapped = rules.MappedExtensions
            .OrderBy(mapping => mapping.Key, StringComparer.Ordinal)
            .Select(mapping => new NhAssistantUploadExtensionDto(mapping.Key, mapping.Value))
            .ToArray();
        return ToDto(agent) with
        {
            Uploads = new NhAssistantAgentUploadsDto(rules.AcceptedMediaTypes, rules.MaxFileBytes, rules.MaxFiles)
            {
                MappedExtensions = mapped
            }
        };
    }

    public static NhAssistantConversationSummaryDto ToSummary(Persistence.NhAssistantConversationListItem item)
    {
        var conversation = item.Conversation;
        return new NhAssistantConversationSummaryDto(
            conversation.Id,
            conversation.AgentId,
            conversation.Title,
            conversation.Status,
            conversation.CreatedAt,
            conversation.UpdatedAt,
            item.Role,
            item.ParticipantCount,
            item.LastMessageSequence,
            item.LastReadSequence,
            conversation.ActiveTurnId is null ? null : conversation.ActiveActorId ?? conversation.OwnerActorId);
    }

    /// <param name="currentActorId">The caller.</param>
    /// <param name="shareToken">The unprotected invitation token for the owner, otherwise <see langword="null"/>.</param>
    /// <param name="thread">The caller's standing in an agent thread, otherwise <see langword="null"/>.</param>
    public static NhAssistantConversationDto ToDto(
        NhAssistantConversationView view,
        string? currentActorId = null,
        string? shareToken = null,
        NhAssistantConversationThreadDto? thread = null)
    {
        return new NhAssistantConversationDto(
            view.Id,
            view.AgentId,
            view.Title,
            view.Status,
            view.CreatedAt,
            view.UpdatedAt,
            view.AgentVersion,
            view.Messages.Select(message => ToDto(message, ChatResolverOf(view, currentActorId))).ToArray(),
            view.PendingApproval is null ? null : ToDto(view.PendingApproval, ChatResolverOf(view, currentActorId)),
            view.Role,
            view.LastReadSequence,
            view.LastMessageSequence,
            view.ActiveActorId,
            view.Members
                .Select(member => new NhAssistantMemberDto(
                    member.ActorId,
                    string.IsNullOrEmpty(member.DisplayName) ? null : member.DisplayName,
                    member.Role,
                    member.JoinedAt))
                .ToArray(),
            shareToken,
            currentActorId,
            view.Kind,
            thread)
        {
            PendingQuestion = view.PendingQuestion is null ? null : ToDto(view.PendingQuestion)
        };
    }

    public static NhAssistantMessageDto ToDto(NhAssistantMessageView message)
    {
        return ToDto(message, null);
    }

    /// <param name="chatActorId">The caller of a chat, who may resolve the uncertain executions they
    /// approved there; <see langword="null"/> elsewhere.</param>
    public static NhAssistantMessageDto ToDto(NhAssistantMessageView message, string? chatActorId)
    {
        return new NhAssistantMessageDto(
            message.Id,
            message.Role,
            message.CreatedAt,
            message.Parts.Select(part => ToDto(part, chatActorId)).ToArray(),
            message.Sequence,
            // An agent message names no person; a rule message says so through its author kind.
            message.Role == NhAssistantMessageRoles.User ? message.AuthorActorId : null,
            message.AuthorKind)
        {
            Application = message.Application is { } application
                ? new NhAssistantApplicationMessageDto(
                    application.Title,
                    application.SenderName,
                    application.LinkUrl is { } url ? new NhAssistantApplicationMessageLinkDto(url, application.LinkLabel) : null,
                    application.Trusted)
                : null,
            SkillId = message.SkillId
        };
    }

    public static NhAssistantMessagePartDto ToDto(NhAssistantPartView part)
    {
        return ToDto(part, null);
    }

    /// <summary>
    /// The caller who may resolve uncertain executions in <paramref name="view"/>: the caller of a chat;
    /// an agent thread's uncertain executions are resolved through <c>approvals/{id}/resolve</c>.
    /// </summary>
    private static string? ChatResolverOf(NhAssistantConversationView view, string? currentActorId)
    {
        return view.Kind == NhAssistantConversationKinds.Chat ? currentActorId : null;
    }

    /// <summary>
    /// Whether <paramref name="chatActorId"/> may resolve the approval in the chat (LS17): it is uncertain,
    /// it was handed to the application and they approved it.
    /// </summary>
    private static bool MayResolve(NhAssistantApprovalView approval, string? chatActorId)
    {
        return chatActorId is not null
            && approval.Status == NhAssistantApprovalStatuses.ExecutionUnknown
            && approval.ExecutionReportDueAt is not null
            && string.Equals(approval.DecidedByActorId, chatActorId, StringComparison.Ordinal);
    }

    public static NhAssistantMessagePartDto ToDto(NhAssistantPartView part, string? chatActorId)
    {
        return part switch
        {
            NhAssistantTextPartView text => new NhAssistantTextPartDto(text.Text),
            NhAssistantConnectionRequiredPartView connection => new NhAssistantConnectionRequiredPartDto(
                connection.ServerId,
                connection.DisplayName,
                connection.Reason),
            NhAssistantFilePartView file => new NhAssistantFilePartDto(file.Name, file.MediaType, file.Size),
            NhAssistantQuestionView question => new NhAssistantQuestionPartDto(
                question.QuestionId,
                question.Question,
                question.Choices,
                question.AddresseeActorId,
                question.ExpiresAt,
                question.Status)
            {
                AnswerIndex = question.AnswerIndex,
                ClosedAt = question.ClosedAt
            },
            NhAssistantToolCallPartView tool => new NhAssistantToolCallPartDto(
                tool.InvocationId,
                tool.ToolId,
                tool.ToolVersion,
                tool.DisplayName,
                tool.Status,
                tool.ArgumentsPreview,
                tool.ResultPreview,
                tool.ResultCode)
            {
                GrantSource = tool.GrantSource == NhAiGrantSource.None ? null : NhAssistantGrantSources.Name(tool.GrantSource),
                Restricted = tool.Restricted
            },
            NhAssistantApprovalView approval => new NhAssistantApprovalPartDto(
                approval.ApprovalId,
                approval.ProposalId,
                approval.ProposalHash,
                approval.ToolId,
                approval.Summary,
                NhAssistantApprovalPresentationDto.From(approval.Presentation),
                approval.ArgumentsPreview,
                approval.Targets,
                approval.ExpiresAt,
                approval.Status)
            {
                StaleKind = approval.StaleKind,
                StaleExplanation = approval.StaleExplanation,
                Rationale = approval.Rationale,
                SupersededByApprovalId = approval.SupersededByApprovalId,
                ExecutionResultCode = approval.ExecutionResultCode,
                ExecutionReportDueAt = approval.ExecutionReportDueAt,
                CanResolve = MayResolve(approval, chatActorId) ? true : null,
                Consultation = approval.Consultation is { } origin
                    ? new NhAssistantApprovalConsultationDto(
                        origin.ConsultationId,
                        origin.AgentId,
                        origin.SubjectType,
                        origin.SubjectId,
                        origin.SubjectDisplayName)
                    {
                        LeaveRoute = origin.LeaveRoute,
                        Answer = origin.Answer,
                        MovedToApprovalId = origin.MovedToApprovalId
                    }
                    : null
            },
            _ => throw new InvalidOperationException($"Unknown assistant message part '{part.GetType().Name}'.")
        };
    }
}

/// <summary>
/// The names of grant sources in the API: <c>none</c>, <c>agent</c>, <c>delegated</c>,
/// <c>delegated-read</c> and <c>accountable</c>.
/// </summary>
internal static class NhAssistantGrantSources
{
    public static string Name(NhAiGrantSource grantSource)
    {
        return grantSource == NhAiGrantSource.DelegatedRead
            ? "delegated-read"
            : grantSource.ToString().ToLowerInvariant();
    }
}
