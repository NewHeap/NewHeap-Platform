using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AI.Chat.Runtime;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// Shields results an agent obtained with its own grant from viewers who could not have run the
/// tool themselves: such a viewer sees that the call happened, but not its result or the
/// artifacts it saved. Each tool is checked once per request against the tool's policies and the
/// thread's subject.
/// </summary>
internal sealed class NhAssistantRestrictedResults
{
    private readonly HttpContext _httpContext;
    private readonly NhAssistantThreadAccess _threadAccess;
    private readonly bool _shield;
    private readonly Dictionary<string, bool> _allowed = new(StringComparer.Ordinal);

    private NhAssistantRestrictedResults(HttpContext httpContext, NhAssistantThreadAccess threadAccess)
    {
        _httpContext = httpContext;
        _threadAccess = threadAccess;
        var registration = httpContext.RequestServices.GetRequiredService<NhAssistantRegistrationState>();
        _shield = !registration.AgentRuns.TryGetValue(threadAccess.Thread.AgentId, out var options)
            || options.ElevatedReads == NhAiElevatedReads.Restricted;
    }

    public static NhAssistantRestrictedResults For(HttpContext httpContext, NhAssistantThreadAccess threadAccess)
    {
        return new NhAssistantRestrictedResults(httpContext, threadAccess);
    }

    public static async Task<NhAssistantConversationView> ApplyAsync(
        NhAssistantConversationView view,
        HttpContext httpContext,
        NhAssistantThreadAccess threadAccess)
    {
        var shield = For(httpContext, threadAccess);
        var messages = new List<NhAssistantMessageView>(view.Messages.Count);
        foreach (var message in view.Messages)
        {
            var parts = new List<NhAssistantPartView>(message.Parts.Count);
            foreach (var part in message.Parts)
            {
                if (part is NhAssistantToolCallPartView tool
                    && !await shield.MayViewAsync(tool.GrantSource, tool.ToolId))
                {
                    parts.Add(tool with { ResultPreview = null, Restricted = true });
                    continue;
                }

                parts.Add(part);
            }

            messages.Add(message with { Parts = parts });
        }

        return view with { Messages = messages };
    }

    /// <summary>
    /// Whether the viewer may see what a call with <paramref name="grantSource"/> returned.
    /// </summary>
    public async Task<bool> MayViewAsync(NhAiGrantSource grantSource, string toolId)
    {
        if (grantSource != NhAiGrantSource.Agent || !_shield)
        {
            return true;
        }
        if (_allowed.TryGetValue(toolId, out var cached))
        {
            return cached;
        }

        var thread = _threadAccess.Thread;
        var may = await SatisfiesToolPoliciesAsync(
            _httpContext.RequestServices,
            _httpContext.User,
            new NhAiSubjectAuthorizationResource(_threadAccess.Subject, thread.ScopeKey, thread.DivisionId, ViewPurpose),
            toolId);
        _allowed[toolId] = may;
        return may;
    }

    internal const string ViewPurpose = "view-agent-result";

    /// <summary>
    /// Whether <paramref name="user"/> could have run the tool on the subject: a tool without
    /// policies, or an unknown tool, never counts as allowed.
    /// </summary>
    internal static async Task<bool> SatisfiesToolPoliciesAsync(
        IServiceProvider services,
        System.Security.Claims.ClaimsPrincipal user,
        NhAiSubjectAuthorizationResource resource,
        string toolId)
    {
        var descriptor = services.GetRequiredService<NhAssistantApproverCheck>().FindDescriptor(toolId);
        if (descriptor is null || descriptor.AuthorizationPolicies.Count == 0)
        {
            return false;
        }

        var authorization = services.GetRequiredService<IAuthorizationService>();
        foreach (var policy in descriptor.AuthorizationPolicies)
        {
            if (!(await authorization.AuthorizeAsync(user, resource, policy)).Succeeded)
            {
                return false;
            }
        }

        return true;
    }
}
