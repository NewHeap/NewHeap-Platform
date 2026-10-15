using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AI.Chat.Runtime;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// Shields results an agent obtained with its own grant, or through a delegated read of the
/// accountable person, from viewers who could not have run the tool themselves: such a viewer sees
/// that the call happened, but not its result or the artifacts it saved. A grant is checked against
/// the tool's policies, a delegated read against the policies of the gateway resource it read, once
/// per request and against the thread's subject.
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
                    && !await shield.MayViewAsync(tool.GrantSource, tool.ToolId, tool.ArgumentsJson))
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
    public async Task<bool> MayViewAsync(NhAiGrantSource grantSource, string toolId, string? argumentsJson)
    {
        if (grantSource is not (NhAiGrantSource.Agent or NhAiGrantSource.DelegatedRead) || !_shield)
        {
            return true;
        }
        var services = _httpContext.RequestServices;
        var key = CacheKey(services, grantSource, toolId, argumentsJson);
        if (_allowed.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var thread = _threadAccess.Thread;
        var may = await SatisfiesResultPoliciesAsync(
            services,
            _httpContext.User,
            new NhAiSubjectAuthorizationResource(_threadAccess.Subject, thread.ScopeKey, thread.DivisionId, ViewPurpose),
            grantSource,
            toolId,
            argumentsJson);
        _allowed[key] = may;
        return may;
    }

    /// <summary>
    /// The cache key of a decision: the tool for a grant, the policies of the read resource for a
    /// delegated read.
    /// </summary>
    internal static string CacheKey(IServiceProvider services, NhAiGrantSource grantSource, string toolId, string? argumentsJson)
    {
        if (grantSource != NhAiGrantSource.DelegatedRead)
        {
            return "grant|" + toolId;
        }
        var policies = DelegatedReadPolicies(services, toolId, argumentsJson);
        return policies is null ? "read|unknown|" + toolId : "read|" + string.Join(',', policies);
    }

    /// <summary>
    /// Whether <paramref name="user"/> may see the result: for a grant, could have run the tool on
    /// the subject; for a delegated read, satisfies every policy of the gateway resource operation it
    /// read. Unknown tools and reads, and tools without policies, never count as allowed.
    /// </summary>
    internal static async Task<bool> SatisfiesResultPoliciesAsync(
        IServiceProvider services,
        System.Security.Claims.ClaimsPrincipal user,
        NhAiSubjectAuthorizationResource resource,
        NhAiGrantSource grantSource,
        string toolId,
        string? argumentsJson)
    {
        if (grantSource != NhAiGrantSource.DelegatedRead)
        {
            return await SatisfiesToolPoliciesAsync(services, user, resource, toolId);
        }

        var policies = DelegatedReadPolicies(services, toolId, argumentsJson);
        if (policies is null || policies.Count == 0)
        {
            return false;
        }
        var authorization = services.GetRequiredService<IAuthorizationService>();
        foreach (var policy in policies)
        {
            if (!(await authorization.AuthorizeAsync(user, resource, policy)).Succeeded)
            {
                return false;
            }
        }
        return true;
    }

    private static IReadOnlyList<string>? DelegatedReadPolicies(IServiceProvider services, string toolId, string? argumentsJson)
    {
        return services.GetServices<INhAiDelegatedReadSurface>()
            .Select(surface => surface.ResultPolicies(toolId, argumentsJson))
            .FirstOrDefault(policies => policies is not null);
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
