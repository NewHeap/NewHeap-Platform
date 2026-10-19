using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AI.Chat.Runtime;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// Shields results an agent obtained with its own grant, or through a delegated read, from viewers
/// who could not have read them themselves: such a viewer sees that the call happened, but not its
/// result or the artifacts it saved. A grant is checked against the tool's policies and the thread's
/// subject, once per tool and request. A delegated read is shown only to the person whose rights it
/// read with, whatever the agent's elevated reads allow: endpoint policies do not capture the rows a
/// controller filters per person.
/// </summary>
internal sealed class NhAssistantRestrictedResults
{
    private readonly HttpContext _httpContext;
    private readonly NhAssistantThreadAccess _threadAccess;
    private readonly bool _shield;
    private readonly Dictionary<string, bool> _allowed = new(StringComparer.Ordinal);
    private string? _viewerActorId;
    private bool _viewerResolved;

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
                    && !await shield.MayViewAsync(tool.GrantSource, tool.ToolId, tool.ReadForActorId))
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
    public async Task<bool> MayViewAsync(NhAiGrantSource grantSource, string toolId, string? readForActorId)
    {
        if (grantSource == NhAiGrantSource.DelegatedRead)
        {
            return IsReadFor(await ViewerActorIdAsync(), readForActorId);
        }
        if (grantSource is not (NhAiGrantSource.Agent or NhAiGrantSource.Accountable) || !_shield)
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

    /// <summary>
    /// Whether <paramref name="viewerActorId"/> is the person a delegated read used the rights of.
    /// An unknown person or viewer never is.
    /// </summary>
    internal static bool IsReadFor(string? viewerActorId, string? readForActorId)
    {
        return !string.IsNullOrWhiteSpace(viewerActorId)
            && !string.IsNullOrWhiteSpace(readForActorId)
            && string.Equals(viewerActorId, readForActorId, StringComparison.Ordinal);
    }

    /// <summary>The viewer's actor id as the assistant's endpoints resolve the caller.</summary>
    private async Task<string?> ViewerActorIdAsync()
    {
        if (!_viewerResolved)
        {
            _viewerResolved = true;
            var caller = await _httpContext.RequestServices.GetRequiredService<INhAiAuthenticatedInvocationContextResolver>()
                .ResolveAsync(_httpContext, _httpContext.RequestAborted);
            _viewerActorId = caller.Success ? caller.Data!.ActorId : null;
        }
        return _viewerActorId;
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
