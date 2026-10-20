using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.Common.Actors;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// The platform's access resolver of the whole application (<see cref="NhAiSubjectRef.SystemType"/>):
/// <c>nh-system/global</c> for agents that work app-wide and <c>nh-system/division-{id}</c> for agents
/// that work per division, when the application lists its divisions. People who pass the read policy
/// (<c>UseSystemSubject</c>, by default the assistant's access policy) read its threads; people who
/// pass the approve policy (by default the administration policy) also take part and decide
/// proposals. Policies are evaluated for the actor as the principal factory rebuilds it, as for
/// headless runs, never from the request, and against the subject with its division: app-wide no
/// division policy is met, in a division it is evaluated for that division. A division subject needs
/// access to the division as well. Who may run an agent for it by hand is the agent's run policy,
/// evaluated the same way (<see cref="MayRunAsync"/>), and who sees its results is the agent's view
/// policy, by default its required policy (<see cref="MayViewAsync"/>).
/// With listed divisions people find the divisions by name, as the subjects of a per-division agent.
/// </summary>
/// <remarks>
/// The whole application lists no members: who passes a policy cannot be listed without the user
/// store, so its member snapshot stays empty. Listings that follow the snapshot, such as the approval
/// inbox and the subject summary, ask the caller's live role on its subjects instead
/// (<see cref="NhAssistantSystemApprovers"/>), and notifications reach its approvers among the people
/// of the thread. Roles are kept for a few seconds per scope, so a listing asks the principal factory
/// once per person.
/// </remarks>
internal sealed class NhAssistantSystemSubjectResolver(
    IServiceProvider services,
    NhAssistantRegistrationState state,
    IOptionsMonitor<NhAssistantOptions> options,
    IAuthorizationService authorization) : INhAssistantPlatformSubjectResolver, INhAiSubjectSearch
{
    /// <summary>
    /// The purpose of the authorization resource the policies are evaluated against.
    /// </summary>
    internal const string Purpose = "assistant-subject-access";

    private const int MaxDisplayNameLength = 200;
    private const int MaxCachedRoles = 256;
    private static readonly TimeSpan RoleLifetime = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<(string SubjectId, string ActorId), (NhAiSubjectAccess Access, long At)> _roles = new();

    public string SubjectType => NhAiSubjectRef.SystemType;

    /// <summary>
    /// The divisions are found by name only when the application lists them.
    /// </summary>
    public bool OffersSearch => services.GetService<INhDivisionDirectory>() is not null;

    public async ValueTask<NhAiSubjectInfo?> DescribeAsync(
        NhAiSubjectRef subject,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        if (NhAiSystemSubjects.IsGlobal(subject))
        {
            // Threads store the name in the fallback language, whatever the culture of the job or
            // request that creates them; the screens name the whole application in the reader's
            // language.
            return new NhAiSubjectInfo(subject, NhAiSubjectInfo.CreateScopeKey(null, null))
            {
                DisplayName = NhAssistantSystemSubjectType.NameIn(CultureInfo.InvariantCulture)
            };
        }
        if (!NhAiSystemSubjects.TryGetDivision(subject, out var divisionId)
            || services.GetService<INhDivisionDirectory>() is not { } directory)
        {
            // Without a division directory the application lists no divisions to work in.
            return null;
        }

        var divisions = await directory.GetAsync([divisionId], cancellationToken);
        if (!divisions.TryGetValue(divisionId, out var division))
        {
            return null;
        }

        return Describe(division);
    }

    public async ValueTask<NhAiSubjectAccess> ResolveAsync(
        NhAiSubjectRef subject,
        string actorId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        if (string.IsNullOrWhiteSpace(actorId))
        {
            return NhAiSubjectAccess.None;
        }

        var key = (subject.Id, actorId);
        var now = Environment.TickCount64;
        if (_roles.TryGetValue(key, out var cached) && now - cached.At < (long)RoleLifetime.TotalMilliseconds)
        {
            return cached.Access;
        }

        var access = await ResolveCoreAsync(subject, actorId, cancellationToken);
        if (_roles.Count >= MaxCachedRoles)
        {
            _roles.Clear();
        }
        _roles[key] = (access, now);
        return access;
    }

    public async IAsyncEnumerable<NhAiSubjectMember> ListMembersAsync(
        NhAiSubjectRef subject,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    /// <summary>
    /// The listed divisions whose name contains the query, as the subjects of the whole application in
    /// those divisions; the search keeps those the person may read. The whole application app-wide is
    /// chosen as such and never searched.
    /// </summary>
    public async IAsyncEnumerable<NhAiSubjectInfo> SearchAsync(
        NhAiSubjectSearchRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (services.GetService<INhDivisionDirectory>() is not { } directory)
        {
            yield break;
        }

        foreach (var division in await directory.ListAsync(NhDivisionInfo.MaxListLimit, cancellationToken))
        {
            if (division.Name.Contains(request.Query, StringComparison.CurrentCultureIgnoreCase))
            {
                yield return Describe(division);
            }
        }
    }

    /// <summary>
    /// Whether the person may run an agent by hand for <paramref name="subject"/> under
    /// <paramref name="runPolicy"/>: the agent's run policy, or the administration policy without one
    /// (K9). It is evaluated as the roles are: for the person as the principal factory rebuilds them,
    /// against the subject with its division, and in a division only for people who may work there.
    /// </summary>
    public async Task<bool> MayRunAsync(
        NhAiSubjectRef subject,
        string actorId,
        string runPolicy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(runPolicy);
        if (string.IsNullOrWhiteSpace(actorId))
        {
            return false;
        }

        if (await PersonAsync(subject, actorId, cancellationToken) is not { } person)
        {
            return false;
        }

        return (await authorization.AuthorizeAsync(person.Principal, person.Resource, runPolicy)).Succeeded;
    }

    /// <summary>
    /// Whether the person may see an agent's results for <paramref name="subject"/> (K14): the agent's
    /// view policy, otherwise its required policy, evaluated as the roles are: for the person as the
    /// principal factory rebuilds them, against the subject with its division, and in a division only
    /// for people who may work there. Without either policy every person who may work in the subject's
    /// scope with an account that may sign in sees them.
    /// </summary>
    public async Task<bool> MayViewAsync(
        NhAiSubjectRef subject,
        string actorId,
        string? viewPolicy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        if (string.IsNullOrWhiteSpace(actorId))
        {
            return false;
        }

        if (await PersonAsync(subject, actorId, cancellationToken) is not { } person)
        {
            return false;
        }

        return viewPolicy is null
            || (await authorization.AuthorizeAsync(person.Principal, person.Resource, viewPolicy)).Succeeded;
    }

    /// <summary>
    /// The policy a person needs to see an agent's results for the whole application: its view policy,
    /// otherwise its required policy; <see langword="null"/> when it names neither.
    /// </summary>
    internal static string? ViewPolicyOf(NhAssistantAgentRunOptions? runOptions, NhAssistantAgentDefinition? definition)
    {
        return runOptions?.WorksOn.ViewPolicy ?? definition?.RequiredPolicy;
    }

    /// <summary>
    /// The policy a person needs to run the agent by hand for the whole application: its run policy,
    /// otherwise the administration policy.
    /// </summary>
    internal static string RunPolicyOf(
        NhAssistantAgentRunOptions runOptions,
        NhAssistantRegistrationState state,
        NhAssistantOptions options)
    {
        ArgumentNullException.ThrowIfNull(runOptions);
        return runOptions.WorksOn.RunPolicy ?? NhAssistantEndpointOptions.ResolveAdminPolicy(state, options);
    }

    /// <summary>
    /// The policy that makes a person a reader of the whole application: the one
    /// <c>UseSystemSubject</c> names, otherwise the access policy.
    /// </summary>
    internal static string ReadPolicyOf(NhAssistantRegistrationState state, NhAssistantOptions options)
    {
        return state.SystemReadPolicy ?? NhAssistantEndpointOptions.ResolveAccessPolicy(state, options);
    }

    /// <summary>
    /// The policy that makes a person a participant and approver of the whole application: the one
    /// <c>UseSystemSubject</c> names, otherwise the administration policy.
    /// </summary>
    internal static string ApprovePolicyOf(NhAssistantRegistrationState state, NhAssistantOptions options)
    {
        return state.SystemApprovePolicy ?? NhAssistantEndpointOptions.ResolveAdminPolicy(state, options);
    }

    private async ValueTask<NhAiSubjectAccess> ResolveCoreAsync(
        NhAiSubjectRef subject,
        string actorId,
        CancellationToken cancellationToken)
    {
        if (await PersonAsync(subject, actorId, cancellationToken) is not { } person)
        {
            return NhAiSubjectAccess.None;
        }

        // The approve policy makes a participant and approver, who reads as well.
        if ((await authorization.AuthorizeAsync(person.Principal, person.Resource, ApprovePolicyOf(state, options.CurrentValue))).Succeeded)
        {
            return new NhAiSubjectAccess(NhAiSubjectRole.Approver);
        }

        return (await authorization.AuthorizeAsync(person.Principal, person.Resource, ReadPolicyOf(state, options.CurrentValue))).Succeeded
            ? new NhAiSubjectAccess(NhAiSubjectRole.Reader)
            : NhAiSubjectAccess.None;
    }

    /// <summary>
    /// The person as the policies of the whole application see them: the principal a headless run
    /// would get, with the subject as the resource, or <see langword="null"/> when the subject does not
    /// exist, the person may not work in its division or the account may no longer sign in.
    /// </summary>
    private async ValueTask<(ClaimsPrincipal Principal, NhAiSubjectAuthorizationResource Resource)?> PersonAsync(
        NhAiSubjectRef subject,
        string actorId,
        CancellationToken cancellationToken)
    {
        var info = await DescribeAsync(subject, cancellationToken);
        if (info is null)
        {
            return null;
        }

        // The whole application in a division is for people who may work in that division.
        if (info.DivisionId is { } divisionId
            && (services.GetService<INhDivisionAccess>() is not { } divisions
                || !await divisions.HasAccessAsync(new NhActorRef(NhActorKinds.User, actorId), divisionId, cancellationToken)))
        {
            return null;
        }

        // The person as a headless run sees them: an account that may no longer sign in has no role.
        if (services.GetService<INhAiPrincipalFactory>() is not { } principals
            || await principals.CreateAsync(actorId, info, cancellationToken) is not { } principal)
        {
            return null;
        }

        return (principal, NhAiSubjectAuthorizationResource.From(info, Purpose));
    }

    private static NhAiSubjectInfo Describe(NhDivisionInfo division)
    {
        var name = division.Name.Length <= MaxDisplayNameLength ? division.Name : division.Name[..MaxDisplayNameLength];
        return new NhAiSubjectInfo(NhAiSystemSubjects.ForDivision(division.Id), NhAiSubjectInfo.CreateScopeKey(null, division.Id))
        {
            DivisionId = division.Id,
            DisplayName = name
        };
    }
}

/// <summary>
/// The whole application's subjects whose proposals a person may decide, evaluated live because the
/// whole application lists no members: the inbox asks the person's role on each subject of the whole
/// application with a pending proposal, at most <see cref="MaxSubjects"/> of them, and the subject
/// summary on each requested one. Roles are kept for ten seconds per person and subject across
/// requests (<see cref="NhAssistantSystemRoleCache"/>), so a listing that reloads often evaluates the
/// policies at most once per subject and person in that time; every decision is checked live again.
/// </summary>
internal sealed class NhAssistantSystemApprovers(
    NhAssistantRunStore runs,
    NhAssistantSubjectAccessService subjects,
    NhAssistantSystemRoleCache cache)
{
    /// <summary>
    /// The most subjects of the whole application one listing evaluates: the application itself and
    /// its divisions.
    /// </summary>
    internal const int MaxSubjects = 100;

    /// <summary>
    /// The subjects of the whole application with a pending proposal on which the person is an
    /// approver. A person bound to a tenant decides none: the whole application belongs to no tenant,
    /// as its proposals' threads do not.
    /// </summary>
    public async Task<IReadOnlyList<string>> ApproverSubjectsAsync(
        string actorId,
        string? tenantId,
        CancellationToken cancellationToken)
    {
        if (tenantId is not null)
        {
            return [];
        }

        var approverOf = new List<string>();
        foreach (var subjectId in await runs.GetSystemSubjectsWithPendingProposalsAsync(MaxSubjects, cancellationToken))
        {
            if (await RoleAsync(subjectId, actorId, cancellationToken) >= NhAiSubjectRole.Approver)
            {
                approverOf.Add(subjectId);
            }
        }

        return approverOf;
    }

    /// <summary>
    /// The person's live role on each of <paramref name="subjectIds"/> of the whole application, the
    /// first <see cref="MaxSubjects"/> of them; subjects without a role are left out.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, NhAiSubjectRole>> RolesAsync(
        IReadOnlyCollection<string> subjectIds,
        string actorId,
        CancellationToken cancellationToken)
    {
        var roles = new Dictionary<string, NhAiSubjectRole>(StringComparer.Ordinal);
        foreach (var subjectId in subjectIds.Distinct(StringComparer.Ordinal).Take(MaxSubjects))
        {
            var role = await RoleAsync(subjectId, actorId, cancellationToken);
            if (role != NhAiSubjectRole.None)
            {
                roles[subjectId] = role;
            }
        }

        return roles;
    }

    private async Task<NhAiSubjectRole> RoleAsync(string subjectId, string actorId, CancellationToken cancellationToken)
    {
        if (cache.TryGet(subjectId, actorId, out var known))
        {
            return known;
        }

        var access = await subjects.ResolveAsync(new NhAiSubjectRef(NhAiSubjectRef.SystemType, subjectId), actorId, cancellationToken);
        cache.Set(subjectId, actorId, access.Role);
        return access.Role;
    }
}

/// <summary>
/// The roles people had on subjects of the whole application in listings, for ten seconds, at most
/// <see cref="MaxEntries"/> at a time. Only listings read it; authorization asks the resolver.
/// </summary>
internal sealed class NhAssistantSystemRoleCache
{
    internal const int MaxEntries = 1_024;
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<(string SubjectId, string ActorId), (NhAiSubjectRole Role, long At)> _roles = new();

    public bool TryGet(string subjectId, string actorId, out NhAiSubjectRole role)
    {
        if (_roles.TryGetValue((subjectId, actorId), out var cached)
            && Environment.TickCount64 - cached.At < (long)Lifetime.TotalMilliseconds)
        {
            role = cached.Role;
            return true;
        }

        role = NhAiSubjectRole.None;
        return false;
    }

    public void Set(string subjectId, string actorId, NhAiSubjectRole role)
    {
        if (_roles.Count >= MaxEntries)
        {
            _roles.Clear();
        }
        _roles[(subjectId, actorId)] = (role, Environment.TickCount64);
    }
}
