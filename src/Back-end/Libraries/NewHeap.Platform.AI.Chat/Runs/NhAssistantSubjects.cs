using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.Runs;

/// <summary>
/// Reaches the subject access resolvers the application registered and keeps the member snapshot
/// that listings, notifications and live updates use. Authorization never reads the snapshot; it
/// asks the resolver on every request.
/// </summary>
internal sealed class NhAssistantSubjectAccessService(
    IEnumerable<INhAiSubjectAccessResolver> resolvers,
    NhAssistantRunStore runs,
    NhAssistantRegistrationState registration,
    ILogger<NhAssistantSubjectAccessService> logger) : INhAiSubjectAccess
{
    private readonly IReadOnlyDictionary<string, INhAiSubjectAccessResolver> _resolvers = resolvers
        .GroupBy(resolver => resolver.SubjectType, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);

    private IReadOnlyDictionary<string, NhAssistantSubjectType>? _types;

    public IReadOnlyCollection<string> SubjectTypes => _resolvers.Keys.ToArray();

    public bool IsKnownType(string subjectType)
    {
        return _resolvers.ContainsKey(subjectType);
    }

    /// <summary>
    /// The registered subject types with how people see them, ordered by id.
    /// </summary>
    public IReadOnlyList<NhAssistantSubjectType> Types => TypeMap.Values.OrderBy(type => type.Id, StringComparer.Ordinal).ToArray();

    /// <summary>
    /// The registered subject type, or <see langword="null"/> for an unknown one.
    /// </summary>
    public NhAssistantSubjectType? FindType(string subjectType)
    {
        return TypeMap.GetValueOrDefault(subjectType);
    }

    /// <summary>
    /// The search of a subject type, when its resolver implements <see cref="INhAiSubjectSearch"/>.
    /// </summary>
    public INhAiSubjectSearch? FindSearch(string subjectType)
    {
        return _resolvers.GetValueOrDefault(subjectType) as INhAiSubjectSearch;
    }

    /// <summary>
    /// Fails when the subject types do not fit together: a resolver with an invalid or reserved type
    /// (only the platform's own resolver handles the whole application), two resolvers of one type,
    /// or a configuration whose resolver is not registered.
    /// </summary>
    public static void Validate(
        IReadOnlyCollection<INhAiSubjectAccessResolver> registered,
        NhAssistantRegistrationState registrationState)
    {
        ArgumentNullException.ThrowIfNull(registered);
        ArgumentNullException.ThrowIfNull(registrationState);
        foreach (var resolver in registered)
        {
            var type = resolver.SubjectType;
            if (!NhAiSubjectRef.IsValidType(type))
            {
                throw new InvalidOperationException(
                    $"The subject access resolver {resolver.GetType().Name} handles '{type}', which is not a dash-case subject type of at most {NhAiSubjectRef.MaxTypeLength} characters.");
            }
            if (NhAiSubjectRef.IsReservedType(type)
                && !(resolver is INhAssistantPlatformSubjectResolver && string.Equals(type, NhAiSubjectRef.SystemType, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"The subject access resolver {resolver.GetType().Name} handles '{type}'; subject types starting with '{NhAiSubjectRef.ReservedTypePrefix}' are reserved for the platform. Rename the type.");
            }
        }

        foreach (var group in registered.GroupBy(resolver => resolver.SubjectType, StringComparer.Ordinal))
        {
            var classes = group.Select(resolver => resolver.GetType()).Distinct().ToArray();
            if (classes.Length > 1)
            {
                throw new InvalidOperationException(
                    $"Subject type '{group.Key}' has more than one access resolver: {string.Join(", ", classes.Select(item => item.Name))}. Register one resolver per subject type.");
            }
        }

        var registeredClasses = registered.Select(resolver => resolver.GetType()).ToHashSet();
        foreach (var options in registrationState.SubjectTypes.Values.Where(options => options.IsConfigured))
        {
            if (!registeredClasses.Contains(options.ResolverType))
            {
                throw new InvalidOperationException(
                    $"The subject type of {options.ResolverType.Name} is configured, but {options.ResolverType.Name} is not registered as a subject access resolver.");
            }
        }
    }

    private IReadOnlyDictionary<string, NhAssistantSubjectType> TypeMap => _types ??= _resolvers
        .ToDictionary(
            entry => entry.Key,
            entry => new NhAssistantSubjectType(
                entry.Key,
                entry.Value.GetType(),
                registration.SubjectTypes.GetValueOrDefault(entry.Value.GetType()),
                entry.Value is INhAiSubjectSearch,
                NhAiSubjectRef.IsReservedType(entry.Key)),
            StringComparer.Ordinal);

    public async ValueTask<NhAiSubjectInfo?> DescribeAsync(
        NhAiSubjectRef subject,
        CancellationToken cancellationToken)
    {
        if (!_resolvers.TryGetValue(subject.Type, out var resolver))
        {
            return null;
        }

        var info = await resolver.DescribeAsync(subject, cancellationToken);
        if (info is null)
        {
            return null;
        }

        if (info.Subject != subject || string.IsNullOrWhiteSpace(info.ScopeKey) || info.ScopeKey.Length > 128)
        {
            throw new InvalidOperationException(
                $"The subject access resolver for '{subject.Type}' returned an invalid description.");
        }

        return info;
    }

    public async ValueTask<NhAiSubjectAccess> ResolveAsync(
        NhAiSubjectRef subject,
        string actorId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(actorId) || !_resolvers.TryGetValue(subject.Type, out var resolver))
        {
            return NhAiSubjectAccess.None;
        }

        return await resolver.ResolveAsync(subject, actorId, cancellationToken);
    }

    public async Task<TaskResult> RefreshAsync(
        NhAiSubjectRef subject,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        if (!_resolvers.TryGetValue(subject.Type, out var resolver))
        {
            return TaskResult.Failed(NhAssistantErrorCodes.SubjectTypeUnknown, "The subject type has no access resolver.");
        }

        var info = await DescribeAsync(subject, cancellationToken);
        if (info is null)
        {
            return TaskResult.Failed(NhAssistantErrorCodes.SubjectNotFound, "The subject was not found.");
        }

        var members = new List<NhAiSubjectMember>();
        await foreach (var member in resolver.ListMembersAsync(subject, cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(member.ActorId) || member.ActorId.Length > 256)
            {
                continue;
            }

            members.Add(member);
        }

        await runs.ReplaceMembersAsync(info.ScopeKey, subject, members, DateTimeOffset.UtcNow, cancellationToken);
        foreach (var thread in await runs.FindThreadsForSubjectAsync(info.ScopeKey, subject, cancellationToken))
        {
            await runs.UpdateThreadSubjectAsync(thread.ConversationId, info, cancellationToken);
        }

        logger.LogDebug(
            "Refreshed {MemberCount} members of subject type {SubjectType}.",
            members.Count,
            subject.Type);
        return TaskResult.Succeeded();
    }
}

/// <summary>
/// The live access of one actor to one agent thread.
/// </summary>
internal sealed record NhAssistantThreadAccess(
    AssistantThread Thread,
    NhAiSubjectRef Subject,
    NhAiSubjectAccess Access,
    bool Invited)
{
    /// <summary>
    /// The role the actor has in the thread: the subject role, or participant through an invitation.
    /// </summary>
    public NhAiSubjectRole Role => Invited && Access.Role < NhAiSubjectRole.Participant
        ? NhAiSubjectRole.Participant
        : Access.Role;
}

/// <summary>
/// Creates and finds agent threads and decides who may use them. Thread access follows the
/// subject access resolver on every request; invitations add participants, never approvers.
/// </summary>
internal sealed class NhAssistantThreadService(
    NhAssistantRunStore runs,
    INhAssistantStore store,
    NhAssistantSubjectAccessService subjects,
    NhAssistantRegistrationState registration)
{
    /// <summary>
    /// Returns the live thread of the agent about the subject, or with <paramref name="shadow"/> its
    /// shadow thread, creating it when needed.
    /// </summary>
    public async Task<TaskResult<AssistantThread>> GetOrCreateAsync(
        NhAssistantAgent agent,
        NhAiSubjectInfo subject,
        CancellationToken cancellationToken,
        bool shadow = false)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(subject);
        var existing = await runs.FindThreadAsync(subject.ScopeKey, agent.Id, subject.Subject, cancellationToken, shadow);
        if (existing is not null)
        {
            var current = registration.RunOptionsOf(agent.Id);
            var accountability = NhAssistantAccountabilities.From(current.Accountability);
            if (current.DataVersion is not null
                && !string.Equals(existing.Accountability, accountability, StringComparison.Ordinal)
                && ResolveAccountableOwner(agent, subject) is { Success: true } resolved)
            {
                // The run settings of an agent created in the administration changed whom it answers
                // to: its threads follow, as they follow a replaced technical account.
                await runs.SetAccountabilityOfThreadAsync(existing.ConversationId, accountability, resolved.Data!, cancellationToken);
                existing.Accountability = accountability;
                existing.AccountableOwnerId = resolved.Data!;
            }

            return TaskResult<AssistantThread>.Succeeded(existing);
        }

        var accountable = ResolveAccountableOwner(agent, subject);
        if (!accountable.Success)
        {
            return TaskResult<AssistantThread>.Failed(accountable);
        }

        var options = registration.RunOptionsOf(agent.Id);
        var now = DateTimeOffset.UtcNow;
        var conversation = new AssistantConversation
        {
            Id = Guid.NewGuid(),
            Kind = NhAssistantConversationKinds.Thread,
            OwnerActorId = agent.Definition.ActorId,
            TenantId = subject.TenantId,
            AgentId = agent.Id,
            AgentVersion = agent.Definition.Version,
            Title = Bound(subject.DisplayName ?? subject.Subject.ToString(), 200),
            Status = NhAssistantConversationStatuses.Idle,
            CreatedAt = now,
            UpdatedAt = now,
            ConcurrencyStamp = Guid.NewGuid()
        };
        var thread = new AssistantThread
        {
            ConversationId = conversation.Id,
            ScopeKey = subject.ScopeKey,
            AgentId = agent.Id,
            SubjectType = subject.Subject.Type,
            SubjectId = subject.Subject.Id,
            SubjectDisplayName = subject.DisplayName is null ? null : Bound(subject.DisplayName, 200),
            DivisionId = subject.DivisionId,
            TenantId = subject.TenantId,
            SubjectVersion = subject.Version,
            Accountability = NhAssistantAccountabilities.From(options.Accountability),
            AccountableOwnerId = accountable.Data!,
            Shadow = shadow,
            MemorySchemaVersion = options.MemorySchemaVersion,
            MemoryConcurrencyStamp = Guid.NewGuid(),
            CreatedAt = now,
            UpdatedAt = now
        };
        var stored = await runs.AddThreadAsync(conversation, thread, cancellationToken);
        return TaskResult<AssistantThread>.Succeeded(stored);
    }

    /// <summary>
    /// Returns who answers for a run of the agent on the subject: the agent itself, the
    /// subject's responsible person or the technical account, the one an administrator set at
    /// runtime before the one in code.
    /// </summary>
    public TaskResult<string> ResolveAccountableOwner(
        NhAssistantAgent agent,
        NhAiSubjectInfo subject)
    {
        var options = registration.RunOptionsOf(agent.Id);
        switch (options.Accountability)
        {
            case NhAiAgentAccountability.SubjectResponsible:
                return string.IsNullOrWhiteSpace(subject.ResponsibleActorId)
                    ? TaskResult<string>.Failed(
                        NhAssistantErrorCodes.NoAccountableOwner,
                        "The subject has no responsible person to answer for the agent.")
                    : TaskResult<string>.Succeeded(subject.ResponsibleActorId);
            case NhAiAgentAccountability.ServiceAccount:
                return TaskResult<string>.Succeeded(agent.ServiceAccountOverride ?? options.ServiceAccountActorId!);
            default:
                return TaskResult<string>.Succeeded(agent.Definition.ActorId);
        }
    }

    /// <summary>
    /// Returns the actor's live access to a thread conversation, or <see langword="null"/> when the
    /// conversation is no thread or the actor has no role. The participant row that holds read
    /// state follows the role: it is created for members and removed when the role ends.
    /// </summary>
    public async Task<NhAssistantThreadAccess?> ResolveAccessAsync(
        Guid conversationId,
        string actorId,
        string? displayName,
        CancellationToken cancellationToken)
    {
        var thread = await runs.FindThreadByConversationAsync(conversationId, cancellationToken);
        if (thread is null)
        {
            return null;
        }

        var subject = new NhAiSubjectRef(thread.SubjectType, thread.SubjectId);
        var access = await subjects.ResolveAsync(subject, actorId, cancellationToken);
        var participants = await store.GetParticipantsAsync(conversationId, cancellationToken);
        var participant = participants.FirstOrDefault(item => string.Equals(item.ActorId, actorId, StringComparison.Ordinal));
        var invited = participant is not null
            && !string.Equals(participant.JoinedVia, NhAssistantParticipantSources.Subject, StringComparison.Ordinal);

        if (access.Role == NhAiSubjectRole.None)
        {
            if (participant is not null && !invited)
            {
                await store.TryRemoveParticipantAsync(conversationId, actorId, cancellationToken);
            }

            return invited
                ? new NhAssistantThreadAccess(thread, subject, access, true)
                : null;
        }

        if (participant is null)
        {
            await store.TryAddParticipantAsync(
                new AssistantConversationParticipant
                {
                    ConversationId = conversationId,
                    ActorId = actorId,
                    DisplayName = Bound(string.IsNullOrWhiteSpace(displayName) ? actorId : displayName, NhAssistantParticipantNames.MaxLength),
                    JoinedVia = NhAssistantParticipantSources.Subject,
                    JoinedAt = DateTimeOffset.UtcNow
                },
                int.MaxValue,
                cancellationToken);
        }

        return new NhAssistantThreadAccess(thread, subject, access, invited);
    }

    /// <summary>
    /// Returns the actor's live access to <paramref name="thread"/> as <see cref="ResolveAccessAsync"/>
    /// does, without changing who takes part in the conversation: reading a proposal does not join
    /// the thread. <see langword="null"/> when the actor has no role and no invitation.
    /// </summary>
    public async Task<NhAssistantThreadAccess?> ReadAccessAsync(
        AssistantThread thread,
        string actorId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(thread);
        var subject = new NhAiSubjectRef(thread.SubjectType, thread.SubjectId);
        return await ReadAccessAsync(thread, actorId, await subjects.ResolveAsync(subject, actorId, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// The same check with the actor's role on the thread's subject already resolved, so a caller
    /// that reads several threads of one subject asks the resolver once.
    /// </summary>
    public async Task<NhAssistantThreadAccess?> ReadAccessAsync(
        AssistantThread thread,
        string actorId,
        NhAiSubjectAccess access,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(access);
        var subject = new NhAiSubjectRef(thread.SubjectType, thread.SubjectId);
        if (access.Role != NhAiSubjectRole.None)
        {
            return new NhAssistantThreadAccess(thread, subject, access, false);
        }

        // Without a role on the subject only an invitation to the thread's conversation gives access.
        var participants = await store.GetParticipantsAsync(thread.ConversationId, cancellationToken);
        var invited = participants.Any(item => string.Equals(item.ActorId, actorId, StringComparison.Ordinal)
            && !string.Equals(item.JoinedVia, NhAssistantParticipantSources.Subject, StringComparison.Ordinal));
        return invited ? new NhAssistantThreadAccess(thread, subject, access, true) : null;
    }

    private static string Bound(string value, int maximum)
    {
        return value.Length <= maximum ? value : value[..maximum];
    }
}
