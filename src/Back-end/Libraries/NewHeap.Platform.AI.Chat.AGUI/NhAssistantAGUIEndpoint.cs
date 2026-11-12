using System.Text;
using System.Text.Json;
using AGUI.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.Collaboration;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.Common.Models;
using Endpoints = NewHeap.Platform.AI.Chat.AspNet.NhAssistantEndpointRouteBuilderExtensions;

namespace NewHeap.Platform.AI.Chat.AGUI;

/// <summary>
/// One AG-UI endpoint of the assistant: runs the chat agent it serves for the authenticated person over the
/// assistant's own turn runner. Everything the AG-UI request carries is untrusted. The person is the
/// request's identity, never a value of the request; the thread only names a conversation, which access
/// checks as every assistant endpoint does; only the newest message is read, because the conversation's
/// history comes from the assistant's store; <c>tools</c> only selects client tools the application declared
/// by id; and <c>state</c>, <c>context</c> and <c>forwardedProps</c> never reach the turn. A resume entry selects
/// an interrupt the endpoint issued and resumes it with the semantics of the decide, answer and elicitation
/// endpoints, whose checks run unchanged.
/// </summary>
internal sealed class NhAssistantAGUIEndpoint(string agentId)
{
    /// <summary>
    /// The largest request the endpoint reads. An AG-UI client sends the whole conversation with every
    /// request, of which only the newest message is used.
    /// </summary>
    public const long MaxRequestBytes = 4 * 1024 * 1024;

    /// <summary>The most resume entries one request may carry.</summary>
    public const int MaxResumeEntries = 8;

    public string AgentId { get; } = agentId;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var caller = await Endpoints.ResolveCallerAsync(httpContext);
        if (!caller.Success)
        {
            return Endpoints.ContextUnavailable();
        }
        var input = await ReadInputAsync(httpContext);
        if (input is null)
        {
            return Endpoints.Error(NhAssistantErrorCodes.MessageInvalid);
        }
        var conversationId = NhAssistantAGUIThreads.ConversationIdOf(input.ThreadId, caller.Data, AgentId);
        var runId = NhAssistantAGUIThreads.RunIdOf(input.RunId);
        if (conversationId is null || runId is null)
        {
            return Endpoints.Error(NhAssistantErrorCodes.MessageInvalid);
        }

        var run = new NhAssistantAGUIRun(input.ThreadId!, runId, conversationId.Value, caller.Data);
        if (input.Resume is { Count: > 0 } resume)
        {
            return await ResumeAsync(httpContext, run, input, resume);
        }

        return input.Messages?.LastOrDefault() switch
        {
            AGUIUserMessage message => await SendAsync(httpContext, run, input, message),
            AGUIToolMessage result => await ReportClientToolAsync(httpContext, run, input, result),
            _ => Endpoints.Error(NhAssistantErrorCodes.MessageInvalid)
        };
    }

    /// <summary>
    /// Starts a turn with the newest message, a person's text, in the thread's conversation; the first
    /// message of a thread creates the conversation with the endpoint's agent.
    /// </summary>
    private async Task<IResult> SendAsync(HttpContext httpContext, NhAssistantAGUIRun run, RunAgentInput input, AGUIUserMessage message)
    {
        var text = TextOf(message.Content);
        if (text is null)
        {
            // Files reach the assistant through its upload endpoint, never as AG-UI binary input.
            return Endpoints.Error(NhAssistantErrorCodes.MessageInvalid);
        }

        var resolved = await ConversationForMessageAsync(httpContext, run);
        if (resolved.Error is not null)
        {
            return resolved.Error;
        }

        var services = httpContext.RequestServices;
        var displayNames = services.GetRequiredService<INhAssistantDisplayNameResolver>();
        await services.GetRequiredService<NhAssistantCollaboration>().RefreshDisplayNameAsync(
            resolved.Access!,
            await displayNames.GetDisplayNameAsync(httpContext.User, run.Caller, httpContext.RequestAborted),
            httpContext.RequestAborted);

        var started = await services.GetRequiredService<INhAssistantTurnRunner>().StartMessageTurnAsync(
            new NhAssistantMessageTurnRequest(
                run.ConversationId,
                run.Caller,
                text,
                message.Id is { Length: > 0 and <= 128 } clientMessageId ? clientMessageId : null,
                TurnToken(httpContext),
                Endpoints.RequestLanguage(httpContext))
            {
                Person = Endpoints.PersonOf(httpContext, run.Caller),
                ClientTools = ClientToolsOf(httpContext, input)
            },
            httpContext.RequestAborted);
        return started.Success
            ? new NhAssistantAGUIEventStreamResult(started.Data!, MapperOf(httpContext, run))
            : Endpoints.FromFailure(started);
    }

    /// <summary>
    /// Continues the turn that waits for a client tool with the result the AG-UI client reported in its tool
    /// message: the semantics of <c>client-tools/{invocationId}/result</c>. Only the person whose turn called
    /// the tool reports, and the report keeps that endpoint's bounds.
    /// </summary>
    private async Task<IResult> ReportClientToolAsync(HttpContext httpContext, NhAssistantAGUIRun run, RunAgentInput input, AGUIToolMessage result)
    {
        if (!Guid.TryParse(result.ToolCallId, out var invocationId))
        {
            return Endpoints.Error(NhAssistantErrorCodes.ClientToolNotFound);
        }
        var report = ClientToolReportOf(result);
        if (report is null)
        {
            return Endpoints.Error(NhAssistantErrorCodes.ClientToolResultInvalid);
        }
        var resolved = await ExistingConversationAsync(httpContext, run, NhAssistantAgentCheck.Everyone);
        if (resolved.Error is not null)
        {
            return resolved.Error;
        }

        var started = await httpContext.RequestServices.GetRequiredService<INhAssistantTurnRunner>().StartClientToolResultTurnAsync(
            new NhAssistantClientToolResultTurnRequest(
                run.ConversationId,
                run.Caller,
                invocationId,
                report.Value.Status,
                report.Value.Outcome,
                report.Value.Id,
                report.Value.Data,
                TurnToken(httpContext),
                Endpoints.RequestLanguage(httpContext))
            {
                Person = Endpoints.PersonOf(httpContext, run.Caller),
                ClientTools = ClientToolsOf(httpContext, input)
            },
            httpContext.RequestAborted);
        return started.Success
            ? new NhAssistantAGUIEventStreamResult(started.Data!, MapperOf(httpContext, run, invocationId))
            : Endpoints.FromFailure(started);
    }

    /// <summary>
    /// Answers the interrupts the request resumes. Answers to an MCP server's requests for input go to the
    /// tool calls of the running turn, whose own run goes on; at most one entry resumes the turn that waits
    /// for a decision or an answer, and a cancelled one cancels it as <c>conversations/{id}/cancel</c> does.
    /// </summary>
    private async Task<IResult> ResumeAsync(HttpContext httpContext, NhAssistantAGUIRun run, RunAgentInput input, IList<AGUIResume> resume)
    {
        if (resume.Count > MaxResumeEntries)
        {
            return Endpoints.Error(NhAssistantErrorCodes.MessageInvalid);
        }

        var elicitations = new List<(NhAssistantAGUIInterruptRef Interrupt, AGUIResume Entry)>();
        (NhAssistantAGUIInterruptRef Interrupt, AGUIResume Entry)? turn = null;
        foreach (var entry in resume)
        {
            if (entry is null
                || !NhAssistantAGUIInterrupts.TryParse(entry.InterruptId, out var interrupt)
                || entry.Status is not (ResumeStatus.Resolved or ResumeStatus.Cancelled))
            {
                return Endpoints.Error(NhAssistantErrorCodes.MessageInvalid);
            }
            if (interrupt.Kind == NhAssistantAGUIInterruptKind.Elicitation)
            {
                elicitations.Add((interrupt, entry));
            }
            else if (turn is not null)
            {
                // A turn waits for one decision or answer at a time.
                return Endpoints.Error(NhAssistantErrorCodes.MessageInvalid);
            }
            else
            {
                turn = (interrupt, entry);
            }
        }

        var cancels = turn is { Entry.Status: ResumeStatus.Cancelled } && elicitations.Count == 0;
        var resolved = await ExistingConversationAsync(
            httpContext,
            run,
            cancels ? NhAssistantAgentCheck.None : NhAssistantAgentCheck.Everyone,
            subjectCheck: !cancels);
        if (resolved.Error is not null)
        {
            return resolved.Error;
        }

        var services = httpContext.RequestServices;
        foreach (var (interrupt, entry) in elicitations)
        {
            var (action, content) = NhAssistantAGUIInterrupts.ElicitationAnswerOf(entry);
            var answered = await services.GetRequiredService<NhAssistantElicitationAnswerService>().AnswerAsync(
                run.ConversationId,
                interrupt.Id,
                run.Caller.ActorId,
                run.Caller.TenantId,
                action,
                content,
                httpContext.RequestAborted);
            if (!answered.Success)
            {
                return Endpoints.FromFailure(answered);
            }
        }

        var mapper = MapperOf(httpContext, run);
        if (turn is not { } resumed)
        {
            return new NhAssistantAGUIEventStreamResult(mapper, mapper.Short(null));
        }

        var runner = services.GetRequiredService<INhAssistantTurnRunner>();
        if (resumed.Entry.Status == ResumeStatus.Cancelled)
        {
            var cancelled = await runner.CancelAsync(resolved.Access!, httpContext.RequestAborted);
            return cancelled.Success
                ? new NhAssistantAGUIEventStreamResult(mapper, mapper.Short(new RunFinishedCancelledOutcome()))
                : Endpoints.FromFailure(cancelled);
        }

        if (resumed.Interrupt.Kind == NhAssistantAGUIInterruptKind.Approval)
        {
            if (NhAssistantAGUIInterrupts.DecisionOf(resumed.Entry.Payload) is not { } decision)
            {
                return Endpoints.Error(NhAssistantErrorCodes.ApprovalDecisionInvalid);
            }

            var decided = await Endpoints.StartDecisionTurnAsync(
                httpContext,
                runner,
                run.ConversationId,
                resumed.Interrupt.Id,
                run.Caller,
                new NhAssistantDecideApprovalRequest(decision.Decision, resumed.Interrupt.ProposalHash, decision.Reason)
                {
                    ClientTools = ToolNamesOf(input)
                });
            if (decided.Error is not null)
            {
                return decided.Error;
            }

            // The resumed turn starts the decided call again; the client knows it from the interrupted run.
            var approval = await services.GetRequiredService<INhAssistantStore>().FindApprovalAsync(resumed.Interrupt.Id, httpContext.RequestAborted);
            return new NhAssistantAGUIEventStreamResult(
                decided.Handle!,
                MapperOf(httpContext, run, decidedCall: approval?.ConversationId == run.ConversationId ? approval.ToolInvocationId : (Guid?)null));
        }

        if (NhAssistantAGUIInterrupts.ChoiceOf(resumed.Entry.Payload) is not { } choice)
        {
            return Endpoints.Error(NhAssistantErrorCodes.QuestionAnswerInvalid);
        }

        var started = await runner.StartAnswerTurnAsync(
            new NhAssistantAnswerTurnRequest(
                run.ConversationId,
                run.Caller,
                resumed.Interrupt.Id,
                choice,
                TurnToken(httpContext),
                Endpoints.RequestLanguage(httpContext))
            {
                Person = Endpoints.PersonOf(httpContext, run.Caller),
                ClientTools = ClientToolsOf(httpContext, input)
            },
            httpContext.RequestAborted);
        return started.Success
            ? new NhAssistantAGUIEventStreamResult(started.Data!, mapper)
            : Endpoints.FromFailure(started);
    }

    /// <summary>
    /// The caller's access to the thread's conversation for a new message: the conversation the person may
    /// use with the endpoint's agent, or a new one with the thread's id when no conversation has it. A
    /// conversation of someone else, of another agent or an agent's thread answers as one that does not exist.
    /// </summary>
    private async Task<(NhAssistantConversationAccess? Access, IResult? Error)> ConversationForMessageAsync(
        HttpContext httpContext,
        NhAssistantAGUIRun run)
    {
        var store = httpContext.RequestServices.GetRequiredService<INhAssistantStore>();
        if (await store.FindShareableConversationAsync(run.ConversationId, httpContext.RequestAborted) is null)
        {
            var created = await CreateConversationAsync(httpContext, run);
            if (created is not null)
            {
                return (null, created);
            }
        }

        return await ExistingConversationAsync(httpContext, run, NhAssistantAgentCheck.Everyone);
    }

    /// <summary>
    /// Checks the caller's access to the thread's conversation as the assistant endpoints check it, and that
    /// it is a person's chat with the endpoint's agent. Returns the access, or the error response.
    /// </summary>
    private async Task<(NhAssistantConversationAccess? Access, IResult? Error)> ExistingConversationAsync(
        HttpContext httpContext,
        NhAssistantAGUIRun run,
        NhAssistantAgentCheck agentCheck,
        bool subjectCheck = true)
    {
        var resolved = await Endpoints.ResolveAccessAsync(httpContext, run.ConversationId, run.Caller, agentCheck, subjectCheck);
        if (resolved.Error is not null)
        {
            return (null, resolved.Error);
        }

        // An agent's thread and a chat with another agent are not this endpoint's to run.
        return Endpoints.ThreadAccessOf(httpContext) is not null
            || !string.Equals(resolved.Access!.Conversation.AgentId, AgentId, StringComparison.Ordinal)
                ? (null, Endpoints.Error(NhAssistantErrorCodes.ConversationNotFound))
                : (resolved.Access, null);
    }

    /// <summary>
    /// Creates the person's chat with the endpoint's agent under the thread's conversation id, with the checks
    /// of <c>POST conversations</c>. Returns the error response, or <see langword="null"/>.
    /// </summary>
    private async Task<IResult?> CreateConversationAsync(HttpContext httpContext, NhAssistantAGUIRun run)
    {
        var services = httpContext.RequestServices;
        var cancellationToken = httpContext.RequestAborted;
        var effective = await services.GetRequiredService<NhAssistantAgentCatalog>().FindAsync(AgentId, includeDisabled: false, cancellationToken);
        if (effective is null || !effective.OfferedInChat)
        {
            return Endpoints.Error(NhAssistantErrorCodes.AgentNotFound);
        }
        if (!await services.GetRequiredService<NhAssistantAgentAccess>().CanUseAsync(httpContext.User, effective.Definition))
        {
            return Endpoints.Error(NhAssistantErrorCodes.AgentForbidden);
        }
        var subject = await NhAssistantNewChats.CheckSubjectAsync(services, effective, null, run.Caller, cancellationToken);
        if (!subject.Success)
        {
            return Endpoints.FromFailure(subject);
        }

        var ownerName = NhAssistantParticipantNames.Normalize(
            await services.GetRequiredService<INhAssistantDisplayNameResolver>().GetDisplayNameAsync(httpContext.User, run.Caller, cancellationToken));
        var conversation = NhAssistantNewChats.Create(run.Caller, ownerName, effective.Definition, null, subject.Data);
        conversation.Id = run.ConversationId;
        try
        {
            await services.GetRequiredService<INhAssistantStore>().AddConversationAsync(conversation, cancellationToken);
        }
        catch (DbUpdateException)
        {
            // The id is taken, by an archived conversation or one created at the same moment.
            return Endpoints.Error(NhAssistantErrorCodes.ConversationNotFound);
        }

        return null;
    }

    private static NhAssistantAGUIEventMapper MapperOf(
        HttpContext httpContext,
        NhAssistantAGUIRun run,
        Guid? reportedClientTool = null,
        Guid? decidedCall = null)
    {
        return new NhAssistantAGUIEventMapper(
            run.ThreadId,
            run.RunId,
            run.ConversationId,
            httpContext.RequestServices.GetRequiredService<NhAssistantRegistrationState>())
        {
            ReportedClientTool = reportedClientTool,
            DecidedCall = decidedCall
        };
    }

    /// <summary>
    /// The client tools the AG-UI client can run: the names of the request's <c>tools</c> that are ids of
    /// client tools the application declared. Their descriptions and schemas are never read.
    /// </summary>
    private static IReadOnlySet<string> ClientToolsOf(HttpContext httpContext, RunAgentInput input)
    {
        return Endpoints.ClientToolsOf(httpContext, ToolNamesOf(input));
    }

    private static IReadOnlyList<string>? ToolNamesOf(RunAgentInput input)
    {
        return input.Tools?
            .Take(NhAssistantClientToolLimits.MaxDeclaredTools)
            .Select(tool => tool?.Name)
            .OfType<string>()
            .ToArray();
    }

    private static CancellationToken TurnToken(HttpContext httpContext)
    {
        return Endpoints.KeepsTurnAfterDisconnect(httpContext) ? CancellationToken.None : httpContext.RequestAborted;
    }

    /// <summary>
    /// The text of a person's message: its text or its text parts; <see langword="null"/> when it carries
    /// anything else.
    /// </summary>
    private static string? TextOf(AGUIContent content)
    {
        if (content.IsText)
        {
            return content.Value as string ?? string.Empty;
        }

        var text = new StringBuilder();
        foreach (var part in content)
        {
            if (part is not AGUITextInputContent textPart)
            {
                return null;
            }
            text.Append(textPart.Text);
        }
        return text.ToString();
    }

    /// <summary>
    /// What the AG-UI client's tool message reports about a client tool: the assistant's report object
    /// (<c>status</c>, <c>outcome</c>, <c>id</c>, <c>data</c>) when its content is one, a failure when the message
    /// carries an error, and otherwise a completed call without data. <see langword="null"/> for a report
    /// object whose fields have the wrong types.
    /// </summary>
    private static (string Status, string? Outcome, string? Id, JsonElement? Data)? ClientToolReportOf(AGUIToolMessage result)
    {
        if (!string.IsNullOrEmpty(result.Error))
        {
            return (NhAssistantClientToolStatuses.Failed, null, null, null);
        }

        var content = result.Content.IsText ? result.Content.Value as string : null;
        if (string.IsNullOrWhiteSpace(content))
        {
            return (NhAssistantClientToolStatuses.Completed, null, null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            var report = document.RootElement;
            if (report.ValueKind != JsonValueKind.Object
                || !report.TryGetProperty("status", out var status))
            {
                return (NhAssistantClientToolStatuses.Completed, null, null, null);
            }
            if (status.ValueKind != JsonValueKind.String
                || !TryGetString(report, "outcome", out var outcome)
                || !TryGetString(report, "id", out var id))
            {
                return null;
            }

            JsonElement? data = report.TryGetProperty("data", out var value) && value.ValueKind != JsonValueKind.Null
                ? value.Clone()
                : null;
            return (status.GetString()!, outcome, id, data);
        }
        catch (JsonException)
        {
            return (NhAssistantClientToolStatuses.Completed, null, null, null);
        }
    }

    private static bool TryGetString(JsonElement report, string name, out string? value)
    {
        value = null;
        if (!report.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }
        if (property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();
        return true;
    }

    private static async Task<RunAgentInput?> ReadInputAsync(HttpContext httpContext)
    {
        if (!httpContext.Request.HasJsonContentType())
        {
            return null;
        }
        if (httpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit
            && (limit.MaxRequestBodySize is null || limit.MaxRequestBodySize > MaxRequestBytes))
        {
            limit.MaxRequestBodySize = MaxRequestBytes;
        }
        if (httpContext.Request.ContentLength > MaxRequestBytes)
        {
            return null;
        }

        try
        {
            return await httpContext.Request.ReadFromJsonAsync(AGUIJsonSerializerContext.Default.RunAgentInput, httpContext.RequestAborted);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (AGUIUnknownEventTypeException)
        {
            return null;
        }
    }
}

/// <summary>
/// One AG-UI run: the client's thread and run ids it echoes, the conversation the thread names and the
/// authenticated caller.
/// </summary>
internal sealed record NhAssistantAGUIRun(string ThreadId, string RunId, Guid ConversationId, NhAiInvocationContext Caller);
