using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.Common;
using NewHeap.Platform.Common.Identity.Claims;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// Decides whether a person may make an account the one an agent answers to, whose rights the agent's
/// headless runs then use. The account must exist and may sign in, as the principal factory that
/// rebuilds it for the runs decides. A person may bind it only when they may act for that account:
/// it is the person themself, it is the agent's account in code, the person may act as any user
/// (the platform's impersonation permission), or the person holds every right the account would lend
/// the agent, that is every authorization policy of the agent's tools that the account passes against
/// the agent without a division. A binding by application code with a technical actor id, which the
/// platform knows as no person, is the application's own decision.
/// </summary>
internal sealed class NhAssistantServiceAccountBinding(
    IServiceProvider services,
    IAuthorizationService authorization,
    IEnumerable<INhAiToolCatalog> catalogs)
{
    private const string Purpose = "service-account";

    private readonly IReadOnlyList<INhAiToolCatalog> _catalogs = catalogs.ToArray();

    /// <summary>
    /// Whether <paramref name="callerActorId"/> may make <paramref name="accountActorId"/> the account
    /// the agent answers to: <see cref="NhAssistantErrorCodes.ServiceAccountNotFound"/> for an account
    /// that does not exist or may not sign in, <see cref="NhAssistantErrorCodes.ServiceAccountNotPermitted"/>
    /// for one that would lend the agent rights the caller does not hold.
    /// </summary>
    /// <param name="toolSelectors">The agent's tool selectors, whose tools the account would serve.</param>
    /// <param name="accountInCode">The account the agent answers to in code, which its developers chose;
    /// <see langword="null"/> for an agent without one.</param>
    /// <param name="bound">The account is already the one the agent answers to: only whether it still
    /// exists is checked, since saving it again lends nothing new.</param>
    public async Task<TaskResult> CheckAsync(
        string agentId,
        IReadOnlyList<string> toolSelectors,
        string accountActorId,
        string callerActorId,
        string? accountInCode,
        bool bound,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(toolSelectors);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountActorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(callerActorId);

        var principals = services.GetService<INhAiPrincipalFactory>();
        var subject = new NhAiSubjectInfo(new NhAiSubjectRef("agent", agentId), NhAiSubjectInfo.CreateScopeKey(null, null));
        var account = principals is null ? null : await principals.CreateAsync(accountActorId, subject, cancellationToken);
        if (account is null)
        {
            return TaskResult.Failed(NhAssistantErrorCodes.ServiceAccountNotFound, "No account that may sign in exists with that id.");
        }
        if (bound
            || string.Equals(accountActorId, callerActorId, StringComparison.Ordinal)
            || string.Equals(accountActorId, accountInCode, StringComparison.Ordinal))
        {
            return TaskResult.Succeeded();
        }

        var caller = await CallerAsync(callerActorId, principals!, subject, cancellationToken);
        if (caller is null)
        {
            // Application code that binds with an operator or job id the platform knows as no person.
            return TaskResult.Succeeded();
        }
        if (caller.HasClaim(NhPlatformClaimTypes.Permission, Constants.PermissionClaimValues.AuthImpersonateAllowed))
        {
            // A person who may act as any user lends nothing new by binding one.
            return TaskResult.Succeeded();
        }

        var resource = new NhAiSubjectAuthorizationResource(subject.Subject, subject.ScopeKey, null, Purpose);
        foreach (var descriptor in ToolsOf(toolSelectors))
        {
            if (await PassesAsync(account, resource, descriptor, cancellationToken)
                && !await PassesAsync(caller, resource, descriptor, cancellationToken))
            {
                return TaskResult.Failed(
                    NhAssistantErrorCodes.ServiceAccountNotPermitted,
                    "The account would lend the agent rights that the caller does not hold.");
            }
        }

        return TaskResult.Succeeded();
    }

    /// <summary>
    /// The caller's principal: the request's user when the caller sent the request, otherwise the
    /// principal the factory rebuilds; <see langword="null"/> for a caller the platform knows as no
    /// person.
    /// </summary>
    private async Task<ClaimsPrincipal?> CallerAsync(
        string callerActorId,
        INhAiPrincipalFactory principals,
        NhAiSubjectInfo subject,
        CancellationToken cancellationToken)
    {
        if (services.GetService<IHttpContextAccessor>()?.HttpContext is { } httpContext
            && await NhAiRequestActor.IsAsync(httpContext, callerActorId, services, cancellationToken))
        {
            return httpContext.User;
        }

        return await principals.CreateAsync(callerActorId, subject, cancellationToken);
    }

    /// <summary>
    /// The tools of the agent that an account's rights could serve: exposed to agents, selected by the
    /// agent, with authorization policies.
    /// </summary>
    private IEnumerable<NhAiToolDescriptor> ToolsOf(IReadOnlyList<string> toolSelectors)
    {
        return _catalogs
            .SelectMany(catalog => catalog.Descriptors)
            .Where(descriptor => (descriptor.Exposure & NhAiToolExposure.Agent) != 0
                && descriptor.AuthorizationPolicies.Count > 0
                && toolSelectors.Any(selector => NhAssistantRegistrationState.Covers(selector, descriptor.Id)))
            .DistinctBy(descriptor => (descriptor.Id, descriptor.Version));
    }

    private async Task<bool> PassesAsync(
        ClaimsPrincipal principal,
        NhAiSubjectAuthorizationResource resource,
        NhAiToolDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        foreach (var policy in descriptor.AuthorizationPolicies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!(await authorization.AuthorizeAsync(principal, resource, policy)).Succeeded)
            {
                return false;
            }
        }

        return true;
    }
}
