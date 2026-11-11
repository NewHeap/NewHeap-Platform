using AGUI.Abstractions;
using AGUI.Formatting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AI.Chat.AspNet;

namespace NewHeap.Platform.AI.Chat.AGUI;

public static class NhAssistantAGUIEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps an AG-UI (Agent-User Interaction Protocol) endpoint at <paramref name="pattern"/> that runs the
    /// assistant's chat agent <paramref name="agentId"/>, so AG-UI clients such as CopilotKit, the .NET
    /// <c>AGUIChatClient</c> or Blazor AI components talk to it. A <c>POST</c> of a <c>RunAgentInput</c> streams one
    /// run as AG-UI server-sent events.
    /// <para>
    /// The endpoint is a second surface over the same assistant, as the MCP export is for tools: the turn
    /// runner, tool authorization, approvals, budget and audit stay the assistant's, and it requires the
    /// assistant access policy and the person's authentication as the assistant endpoints do. The AG-UI
    /// request never names a person or an agent. Its <c>threadId</c> names a conversation of the person with
    /// this agent: a GUID is the conversation's id, any other value names the person's own conversation for
    /// that thread, and the first message creates it; another person's conversation answers <c>404</c>. Only
    /// the newest message is read, a person's text; the history comes from the assistant's store. <c>tools</c>
    /// selects client tools the application declared with <c>AddClientTool</c> by id, never with a description
    /// of its own, and <c>state</c>, <c>context</c> and <c>forwardedProps</c> are ignored.
    /// </para>
    /// <para>
    /// Text streams as text messages, a tool call as a tool call with the arguments' preview and the trusted
    /// result card in its result, a client tool as a frontend tool call that the client runs and answers with
    /// a tool message, and the agent's plan as a <c>STATE_SNAPSHOT</c>. A proposal or question the turn waits
    /// for ends the run with an interrupt: a <c>resume</c> entry decides or answers it with the semantics of
    /// <c>approvals/{approvalId}/decide</c> (bound to the proposal hash in the interrupt's id) and
    /// <c>questions/{questionId}/answer</c>. An MCP server's request for input arrives as the <c>CUSTOM</c> event
    /// <see cref="NhAssistantAGUICustomEvents.ElicitationRequested"/> within the run. A failed turn ends with
    /// <c>RUN_ERROR</c> and the assistant's content-free error code; a request the assistant refuses answers
    /// with the status and error of the matching assistant endpoint.
    /// </para>
    /// <para>It needs <c>AddNewHeapAssistant</c>; map one endpoint per agent that AG-UI clients may run.</para>
    /// </summary>
    /// <param name="endpoints">The application's endpoints.</param>
    /// <param name="pattern">The route of the endpoint, such as <c>/api/assistant/agui/project-assistant</c>.</param>
    /// <param name="agentId">The chat agent the endpoint runs.</param>
    public static RouteHandlerBuilder MapNewHeapAssistantAGUI(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        string agentId)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        var state = endpoints.ServiceProvider.GetService<NhAssistantRegistrationState>()
            ?? throw new InvalidOperationException("Call AddNewHeapAssistant before MapNewHeapAssistantAGUI.");
        var options = endpoints.ServiceProvider.GetRequiredService<IOptions<NhAssistantOptions>>().Value;
        var accessPolicy = NhAssistantEndpointOptions.ResolveAccessPolicy(state, options);

        var endpoint = new NhAssistantAGUIEndpoint(agentId);
        return endpoints.MapPost(pattern, Task<IResult> (HttpContext httpContext) => endpoint.HandleAsync(httpContext))
            .RequireAuthorization(accessPolicy)
            .AddEndpointFilter(NhAssistantRequestCulture.MatchLanguageAsync)
            .AddEndpointFilter(NhAssistantEndpointRouteBuilderExtensions.RequireEnabledAsync)
            .WithTags("Assistant")
            .WithName($"NhAssistantRunAGUI-{agentId}")
            .WithSummary($"Run the {agentId} agent over AG-UI")
            .WithDescription("Runs the assistant's chat agent for the caller over the AG-UI protocol and streams the run as AG-UI server-sent events. The threadId names the caller's conversation with this agent (a GUID is the conversation id; the first message creates it); only the newest message is read; tools only selects client tools the application declared; state, context and forwardedProps are ignored. A resume entry decides a proposal or answers a question that ended the previous run with an interrupt, or answers an MCP server's request for input of the running turn. 400 assistant-message-invalid for a request that is not a RunAgentInput, an unsupported message or resume entry; 404 for another person's conversation; the other errors are those of the matching assistant endpoint.")
            .Accepts<RunAgentInput>("application/json")
            .Produces(StatusCodes.Status200OK, contentType: SseEventStreamFormatter.ServerSentEventsMediaType)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status403Forbidden)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status409Conflict);
    }
}
