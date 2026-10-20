using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AI.AspNet;
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
/// access to the division as well. Endpoints add an agent's own run and view policies on top.
/// </summary>
/// <remarks>
/// The whole application lists no members: who passes a policy cannot be listed without the user
/// store, so its member snapshot stays empty and listings that follow the snapshot, such as the
/// approval inbox, show its proposals to their initiator only. Roles are kept for a few seconds per
/// scope, so a listing asks the principal factory once per person.
/// </remarks>
internal sealed class NhAssistantSystemSubjectResolver(
    IServiceProvider services,
    NhAssistantRegistrationState state,
    IOptionsMonitor<NhAssistantOptions> options,
    IAuthorizationService authorization) : INhAssistantPlatformSubjectResolver
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

        var name = division.Name.Length <= MaxDisplayNameLength ? division.Name : division.Name[..MaxDisplayNameLength];
        return new NhAiSubjectInfo(subject, NhAiSubjectInfo.CreateScopeKey(null, divisionId))
        {
            DivisionId = divisionId,
            DisplayName = name
        };
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

    private async ValueTask<NhAiSubjectAccess> ResolveCoreAsync(
        NhAiSubjectRef subject,
        string actorId,
        CancellationToken cancellationToken)
    {
        var info = await DescribeAsync(subject, cancellationToken);
        if (info is null)
        {
            return NhAiSubjectAccess.None;
        }

        // The whole application in a division is for people who may work in that division.
        if (info.DivisionId is { } divisionId
            && (services.GetService<INhDivisionAccess>() is not { } divisions
                || !await divisions.HasAccessAsync(new NhActorRef(NhActorKinds.User, actorId), divisionId, cancellationToken)))
        {
            return NhAiSubjectAccess.None;
        }

        // The person as a headless run sees them: an account that may no longer sign in has no role.
        if (services.GetService<INhAiPrincipalFactory>() is not { } principals
            || await principals.CreateAsync(actorId, info, cancellationToken) is not { } principal)
        {
            return NhAiSubjectAccess.None;
        }

        // The approve policy makes a participant and approver, who reads as well.
        var resource = NhAiSubjectAuthorizationResource.From(info, Purpose);
        if ((await authorization.AuthorizeAsync(principal, resource, ApprovePolicyOf(state, options.CurrentValue))).Succeeded)
        {
            return new NhAiSubjectAccess(NhAiSubjectRole.Approver);
        }

        return (await authorization.AuthorizeAsync(principal, resource, ReadPolicyOf(state, options.CurrentValue))).Succeeded
            ? new NhAiSubjectAccess(NhAiSubjectRole.Reader)
            : NhAiSubjectAccess.None;
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
}
