using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
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
/// platform knows as no person, is the application's own decision. More tools on an agent that answers to an
/// account lend its rights as well, so they are held to the same rule for the tools they add
/// (<see cref="CheckAddedToolsAsync"/>).
/// </summary>
internal sealed class NhAssistantServiceAccountBinding(
    IServiceProvider services,
    IAuthorizationService authorization,
    IEnumerable<INhAiToolCatalog> catalogs) : INhAssistantLentToolsCheck
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

        return await CheckHeldAsync(agentId, account, callerActorId, subject, ToolsOf(toolSelectors), cancellationToken);
    }

    /// <inheritdoc />
    public async Task<TaskResult> CheckAddedToolsAsync(
        string agentId,
        IReadOnlyList<string> selectorsBefore,
        IReadOnlyList<string> selectorsAfter,
        string accountActorId,
        string callerActorId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(selectorsBefore);
        ArgumentNullException.ThrowIfNull(selectorsAfter);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountActorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(callerActorId);

        var principals = services.GetService<INhAiPrincipalFactory>();
        var subject = new NhAiSubjectInfo(new NhAiSubjectRef("agent", agentId), NhAiSubjectInfo.CreateScopeKey(null, null));
        var account = principals is null ? null : await principals.CreateAsync(accountActorId, subject, cancellationToken);
        // An account that may no longer sign in lends nothing, and a person's own account lends what they hold.
        if (account is null || string.Equals(accountActorId, callerActorId, StringComparison.Ordinal))
        {
            return TaskResult.Succeeded();
        }

        var added = ToolsOf(selectorsAfter)
            .Where(descriptor => !selectorsBefore.Any(selector => NhAssistantRegistrationState.Covers(selector, descriptor.Id)));
        return await CheckHeldAsync(agentId, account, callerActorId, subject, added, cancellationToken);
    }

    /// <summary>
    /// Whether the caller holds every right <paramref name="account"/> would lend the agent through
    /// <paramref name="tools"/>; a caller the platform knows as no person and one who may act as any user do.
    /// </summary>
    private async Task<TaskResult> CheckHeldAsync(
        string agentId,
        ClaimsPrincipal account,
        string callerActorId,
        NhAiSubjectInfo subject,
        IEnumerable<NhAiToolDescriptor> tools,
        CancellationToken cancellationToken)
    {
        var caller = await NhAssistantCallerPrincipal.ResolveAsync(services, agentId, callerActorId, cancellationToken);
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
        foreach (var descriptor in tools)
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
