using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.AspNet.Policy;
using NewHeap.Platform.Common.Identity.Claims;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.AspNet;

/// <summary>
/// The authorization resource of an agent tool call: the subject the agent works on and its
/// division. Division policies read the division from it instead of the request header, so
/// they also work in a background run.
/// </summary>
public sealed record NhAiSubjectAuthorizationResource(
    NhAiSubjectRef Subject,
    string ScopeKey,
    Guid? DivisionId,
    string Purpose) : INhDivisionScopedResource
{
    public static NhAiSubjectAuthorizationResource From(NhAiSubjectInfo subject, string purpose)
    {
        ArgumentNullException.ThrowIfNull(subject);
        return new NhAiSubjectAuthorizationResource(subject.Subject, subject.ScopeKey, subject.DivisionId, purpose);
    }
}

/// <summary>
/// Builds a principal for a person when no request of that person is available, so an agent
/// can act with the person's rights in a background run. The principal is used for one
/// authorization decision and never stored.
/// </summary>
public interface INhAiPrincipalFactory
{
    /// <summary>
    /// Returns the principal of <paramref name="actorId"/>, or <see langword="null"/> when the
    /// actor is unknown or no longer allowed to sign in.
    /// </summary>
    ValueTask<ClaimsPrincipal?> CreateAsync(
        string actorId,
        NhAiSubjectInfo subject,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Rebuilds a principal from the NewHeap user store, from the same claims a sign-in issues.
/// Hosts without the NewHeap user manager, or with claims restored through an
/// <c>IClaimsTransformation</c>, register their own <see cref="INhAiPrincipalFactory"/>.
/// </summary>
internal sealed class NhAiUserManagerPrincipalFactory(IServiceProvider services) : INhAiPrincipalFactory
{
    internal const string AuthenticationType = "nh-ai-delegation";

    public async ValueTask<ClaimsPrincipal?> CreateAsync(
        string actorId,
        NhAiSubjectInfo subject,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(actorId, out var userId))
        {
            return null;
        }

        var userManager = services.GetService<INhUserManager>();
        if (userManager is null)
        {
            return null;
        }

        var claims = await userManager.GetValidClaimsByUserIdAsync(userId, true, cancellationToken);
        var canSignIn = claims.Any(claim =>
            claim.Type == NhPlatformClaimTypes.Permission
            && claim.Value == Platform.Common.Constants.PermissionClaimValues.AuthenticatedAccess);
        if (!canSignIn)
        {
            return null;
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, AuthenticationType));
    }
}

/// <summary>
/// Authorizes tool calls of agent turns, with or without an HTTP request. Outside an agent
/// turn it delegates to the request gate unchanged.
/// <list type="bullet">
/// <item>The tool must be exposed to agents.</item>
/// <item>With delegation, the initiator's own rights allow the tool when the initiator passes
/// the tool's policies against the subject resource (<see cref="NhAiGrantSource.Delegated"/>).</item>
/// <item>Otherwise a code grant of the agent registration allows it, provided the tool opts in
/// through <see cref="NhAiToolAttribute.AllowAgentGrant"/> (<see cref="NhAiGrantSource.Agent"/>).</item>
/// </list>
/// A gateway read in a scope that allows gateway reads is allowed first, with the accountable
/// person's rights (<see cref="NhAiGrantSource.DelegatedRead"/>).
/// </summary>
internal sealed class NhAiAgentInvocationGate(
    NhAiAspNetToolInvocationGate requestGate,
    IHttpContextAccessor httpContextAccessor,
    IAuthorizationService authorizationService,
    INhAiPrincipalFactory principalFactory,
    NhAiAspNetRegistrationState state,
    IEnumerable<INhAiDelegatedReadSurface> readSurfaces) : INhAiToolInvocationGate, INhAiAgentToolAuthorizer
{
    public async ValueTask<TaskResult<NhAiInvocationContext>> AuthorizeAsync(
        NhAiToolDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var scope = NhAiAgentInvocationScopes.Current;
        if (scope is null)
        {
            return await requestGate.AuthorizeAsync(descriptor, cancellationToken);
        }

        var decision = await DecideAsync(descriptor, scope, cancellationToken);
        if (decision.Source == NhAiGrantSource.None)
        {
            return TaskResult<NhAiInvocationContext>.Failed(decision.FailureCode!, decision.FailureMessage!);
        }

        return TaskResult<NhAiInvocationContext>.Succeeded(
            CreateContext(scope, decision.Source, decision.Capabilities));
    }

    public async ValueTask<NhAiGrantSource> AuthorizeAsync(
        NhAiToolDescriptor descriptor,
        NhAiAgentInvocationScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(scope);
        var decision = await DecideAsync(descriptor, scope, cancellationToken);
        return decision.Source;
    }

    private async ValueTask<AgentToolDecision> DecideAsync(
        NhAiToolDescriptor descriptor,
        NhAiAgentInvocationScope scope,
        CancellationToken cancellationToken)
    {
        if ((descriptor.Exposure & NhAiToolExposure.Agent) == 0)
        {
            return AgentToolDecision.Denied(
                NhAiAgentFailureCodes.NotAgentExposed,
                "The AI tool is not exposed to agents.");
        }

        // A gateway read in a headless run reads with the accountable person's rights through a
        // delegated read credential; the bridge's controller decides, as in the person's own turn.
        if (scope.AllowGatewayReads
            && descriptor.Effect == NhAiToolEffect.ReadOnly
            && readSurfaces.Any(surface => surface.IsReadTool(descriptor)))
        {
            return AgentToolDecision.Allowed(
                NhAiGrantSource.DelegatedRead,
                new HashSet<string>(descriptor.RequiredCapabilities, StringComparer.Ordinal));
        }

        var resource = NhAiSubjectAuthorizationResource.From(scope.Subject, scope.Purpose);
        var initiator = await ResolveInitiatorAsync(scope, cancellationToken);
        if (initiator is not null
            && await SatisfiesAllAsync(initiator, resource, descriptor.AuthorizationPolicies, cancellationToken))
        {
            var delegatedCapabilities = await GrantCapabilitiesAsync(initiator, resource, cancellationToken);
            return AgentToolDecision.Allowed(NhAiGrantSource.Delegated, delegatedCapabilities);
        }

        var grant = scope.Grants.FirstOrDefault(candidate =>
            candidate.Matches(descriptor.Id)
            && !(candidate.TriggerOnly && scope.IsRequestTurn));
        if (grant is null || !descriptor.AllowAgentGrant)
        {
            return AgentToolDecision.Denied(
                NhAiAgentFailureCodes.NotGranted,
                "The agent is not allowed to use this AI tool.");
        }

        if (descriptor.Effect == NhAiToolEffect.ReadOnly && scope.ElevatedReads == NhAiElevatedReads.Deny)
        {
            return AgentToolDecision.Denied(
                NhAiAgentFailureCodes.ElevatedReadDenied,
                "The agent may not read through its own grant.");
        }

        return AgentToolDecision.Allowed(
            NhAiGrantSource.Agent,
            new HashSet<string>(descriptor.RequiredCapabilities, StringComparer.Ordinal));
    }

    private async ValueTask<ClaimsPrincipal?> ResolveInitiatorAsync(
        NhAiAgentInvocationScope scope,
        CancellationToken cancellationToken)
    {
        if (!scope.DelegateFromInitiator || string.IsNullOrWhiteSpace(scope.InitiatorActorId))
        {
            return null;
        }

        var user = httpContextAccessor.HttpContext?.User;
        if (scope.IsRequestTurn
            && user?.Identity?.IsAuthenticated == true
            && string.Equals(user.FindFirstValue(ClaimTypes.NameIdentifier), scope.InitiatorActorId, StringComparison.Ordinal))
        {
            return user;
        }

        return await principalFactory.CreateAsync(scope.InitiatorActorId, scope.Subject, cancellationToken);
    }

    private async ValueTask<bool> SatisfiesAllAsync(
        ClaimsPrincipal principal,
        NhAiSubjectAuthorizationResource resource,
        IReadOnlyList<string> policies,
        CancellationToken cancellationToken)
    {
        foreach (var policy in policies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var authorization = await authorizationService.AuthorizeAsync(principal, resource, policy);
            if (!authorization.Succeeded)
            {
                return false;
            }
        }

        return true;
    }

    private async ValueTask<HashSet<string>> GrantCapabilitiesAsync(
        ClaimsPrincipal principal,
        NhAiSubjectAuthorizationResource resource,
        CancellationToken cancellationToken)
    {
        var capabilities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var capability in state.Capabilities.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var authorization = await authorizationService.AuthorizeAsync(principal, resource, capability.Value);
            if (authorization.Succeeded)
            {
                capabilities.Add(capability.Key);
            }
        }

        return capabilities;
    }

    private NhAiInvocationContext CreateContext(
        NhAiAgentInvocationScope scope,
        NhAiGrantSource grantSource,
        IReadOnlySet<string> capabilities)
    {
        return CreateContext(state, scope, grantSource, capabilities);
    }

    internal static NhAiInvocationContext CreateContext(
        NhAiAspNetRegistrationState state,
        NhAiAgentInvocationScope scope,
        NhAiGrantSource grantSource,
        IReadOnlySet<string> capabilities)
    {
        var scopeValues = new Dictionary<string, string>(StringComparer.Ordinal);
        var executionScopes = new List<NhAiExecutionScopeEntry>
        {
            new(scope.Subject.Subject.Type, scope.Subject.Subject.Id, scope.Subject.DisplayName)
        };
        if (state.ActiveDivision is { } activeDivision && scope.Subject.DivisionId is { } divisionId)
        {
            var division = divisionId.ToString();
            scopeValues[activeDivision.ScopeKey] = division;
            executionScopes.Add(new NhAiExecutionScopeEntry(activeDivision.ScopeType, division));
        }

        return new NhAiInvocationContext(scope.AgentActorId, scope.Purpose, scopeValues)
        {
            ActorKind = NhAiActorKind.Agent,
            AgentId = scope.AgentId,
            AccountableOwnerId = scope.AccountableOwnerId,
            TenantId = scope.Subject.TenantId,
            RunId = scope.RunId,
            RunAttemptNumber = scope.RunAttemptNumber,
            CorrelationId = scope.CorrelationId ?? scope.RunId,
            ExecutionScopes = executionScopes,
            CapabilityGrants = capabilities,
            ModelProfileName = scope.ModelProfileName,
            PromptVersion = scope.PromptVersion,
            PromptHash = scope.PromptHash,
            AgentVersion = scope.AgentVersion.ToString(CultureInfo.InvariantCulture),
            Deadline = scope.Deadline,
            IdempotencyKey = scope.IdempotencyKey,
            FencingToken = scope.FencingToken,
            ProposalId = scope.ProposalId,
            ApprovalId = scope.ApprovalId,
            GrantSource = grantSource
        };
    }
}

/// <summary>
/// The invocation context of an agent's own reads, with the subject and, when the host maps the
/// active division, the subject's division, exactly as the agent's tool calls get it.
/// </summary>
internal sealed class NhAiAspNetAgentInvocationContextFactory(NhAiAspNetRegistrationState state)
    : INhAiAgentInvocationContextFactory
{
    public NhAiInvocationContext Create(NhAiAgentInvocationScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return NhAiAgentInvocationGate.CreateContext(state, scope, NhAiGrantSource.None, new HashSet<string>(StringComparer.Ordinal));
    }
}

internal sealed record AgentToolDecision(
    NhAiGrantSource Source,
    IReadOnlySet<string> Capabilities,
    string? FailureCode,
    string? FailureMessage)
{
    public static AgentToolDecision Allowed(NhAiGrantSource source, IReadOnlySet<string> capabilities)
    {
        return new AgentToolDecision(source, capabilities, null, null);
    }

    public static AgentToolDecision Denied(string code, string message)
    {
        return new AgentToolDecision(NhAiGrantSource.None, new HashSet<string>(StringComparer.Ordinal), code, message);
    }
}
