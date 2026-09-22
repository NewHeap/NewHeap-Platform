using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using NewHeap.Platform.AI.Chat.Runtime;

namespace NewHeap.Platform.AI.Chat.AspNet;

/// <summary>
/// Writes a turn as server-sent events: <c>event: &lt;name&gt;</c>, <c>data: &lt;json&gt;</c> and an
/// empty line per event, a <c>: keep-alive</c> comment every 15 seconds, and completion after
/// <c>turn.completed</c> or <c>error</c>. A client disconnect cancels the turn through the request
/// token; the runner then releases the conversation consistently.
/// </summary>
internal sealed class NhAssistantServerSentEventsResult(NhAssistantTurnHandle handle) : IResult
{
    internal static TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(15);

    private static readonly byte[] KeepAlive = Encoding.UTF8.GetBytes(": keep-alive\n\n");

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
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
                wait.CancelAfter(KeepAliveInterval);
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
            // The client disconnected; the turn observes the same token and ends cancelled.
        }
        catch (IOException) when (aborted.IsCancellationRequested)
        {
            // The connection closed while an event was being written.
        }

        await handle.Completion;
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

    public static NhAssistantConversationSummaryDto ToSummary(Entities.AssistantConversation conversation)
    {
        return new NhAssistantConversationSummaryDto(
            conversation.Id,
            conversation.AgentId,
            conversation.Title,
            conversation.Status,
            conversation.CreatedAt,
            conversation.UpdatedAt);
    }

    public static NhAssistantConversationDto ToDto(NhAssistantConversationView view)
    {
        return new NhAssistantConversationDto(
            view.Id,
            view.AgentId,
            view.Title,
            view.Status,
            view.CreatedAt,
            view.UpdatedAt,
            view.AgentVersion,
            view.Messages.Select(message => new NhAssistantMessageDto(
                message.Id,
                message.Role,
                message.CreatedAt,
                message.Parts.Select(ToDto).ToArray())).ToArray(),
            view.PendingApproval is null ? null : ToDto(view.PendingApproval));
    }

    public static NhAssistantMessagePartDto ToDto(NhAssistantPartView part)
    {
        return part switch
        {
            NhAssistantTextPartView text => new NhAssistantTextPartDto(text.Text),
            NhAssistantToolCallPartView tool => new NhAssistantToolCallPartDto(
                tool.InvocationId,
                tool.ToolId,
                tool.ToolVersion,
                tool.DisplayName,
                tool.Status,
                tool.ArgumentsPreview,
                tool.ResultPreview,
                tool.ResultCode),
            NhAssistantApprovalView approval => new NhAssistantApprovalPartDto(
                approval.ApprovalId,
                approval.ProposalId,
                approval.ProposalHash,
                approval.ToolId,
                approval.Summary,
                approval.Presentation is null
                    ? null
                    : new NhAssistantApprovalPresentationDto(
                        approval.Presentation.ToolDisplayName,
                        approval.Presentation.Summary,
                        approval.Presentation.Fields
                            .Select(field => new NhAssistantPresentationFieldDto(field.Label, field.Value))
                            .ToArray(),
                        approval.Presentation.Notice),
                approval.ArgumentsPreview,
                approval.Targets,
                approval.ExpiresAt,
                approval.Status),
            _ => throw new InvalidOperationException($"Unknown assistant message part '{part.GetType().Name}'.")
        };
    }
}
