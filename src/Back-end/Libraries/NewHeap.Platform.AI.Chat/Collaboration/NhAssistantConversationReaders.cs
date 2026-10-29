using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;

namespace NewHeap.Platform.AI.Chat.Collaboration;

/// <summary>
/// The read rule of one conversation: its owner and tenant, its agent thread, whether its agent works
/// and the policy the agent requires, and the tenants whose sign-ins reach it.
/// </summary>
/// <param name="Tenants">The tenants of the sign-ins through which the conversation's people reach
/// it: the conversation's own tenant, and for a thread of the whole application also the tenant of the
/// single-tenant mode (<see cref="NhAssistantWholeApplicationTenancy"/>).</param>
internal sealed record NhAssistantConversationReadRule(
    Guid ConversationId,
    string OwnerActorId,
    string? TenantId,
    AssistantThread? Thread,
    bool AgentWorks,
    string? AgentPolicy,
    IReadOnlyList<string?> Tenants)
{
    /// <summary>
    /// The subject of a person's chat about one (FB25), which everyone who reads the chat, its owner
    /// included, needs live access to; <see langword="null"/> for a chat without one and a thread.
    /// </summary>
    public NhAiSubjectRef? ChatSubject { get; init; }
}

/// <summary>
/// Decides who may follow a conversation when something is delivered to them without a request of
/// theirs: live updates, Web Push and in-app notifications. It applies, at send time, the rule the
/// endpoints apply on every request. An agent thread follows its subject's resolver, as the thread
/// endpoints do: a live role, or an invitation that is no subject join, and for the whole application
/// the agent's view policy (<see cref="NhAssistantThreadService.ReadAccessAsync(AssistantThread, string, CancellationToken)"/>).
/// A chat's owner always reads it; a participant reads it while the agent works and the person, as the
/// principal factory rebuilds them, passes its policy. A chat about a subject (FB25) reaches its owner
/// and participants only while the subject's resolver gives them a role on it in the chat's tenant
/// (<see cref="NhAssistantChatSubjects"/>). Agents never receive anything. The tenant rule
/// binds the delivery to the sign-ins of <see cref="NhAssistantConversationReadRule.Tenants"/> where a
/// channel knows the sign-in, as the live hub does.
/// </summary>
internal sealed class NhAssistantConversationReaders(
    NhAssistantRunStore runs,
    NhAssistantThreadService threads,
    NhAssistantAgentCatalog agents,
    NhAssistantRegistrationState registration,
    INhAssistantRecipientRules recipients,
    NhAssistantChatSubjects chatSubjects)
{
    /// <summary>
    /// The read rule of <paramref name="conversation"/>, with its agent thread when it is one.
    /// </summary>
    public async Task<NhAssistantConversationReadRule> RuleOfAsync(
        AssistantConversation conversation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        var thread = string.Equals(conversation.Kind, NhAssistantConversationKinds.Thread, StringComparison.Ordinal)
            ? await runs.FindThreadByConversationAsync(conversation.Id, cancellationToken)
            : null;
        return await RuleOfAsync(conversation, thread, cancellationToken);
    }

    /// <summary>
    /// The read rule of <paramref name="conversation"/> with its agent thread, which is
    /// <see langword="null"/> for a chat.
    /// </summary>
    public async Task<NhAssistantConversationReadRule> RuleOfAsync(
        AssistantConversation conversation,
        AssistantThread? thread,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        if (thread is not null)
        {
            IReadOnlyList<string?> tenants = thread.TenantId is null
                && string.Equals(thread.SubjectType, NhAiSubjectRef.SystemType, StringComparison.Ordinal)
                && registration.SingleTenantId is { } single
                    ? [null, single]
                    : [thread.TenantId];
            return new NhAssistantConversationReadRule(
                conversation.Id,
                conversation.OwnerActorId,
                conversation.TenantId,
                thread,
                true,
                null,
                tenants);
        }

        var agent = await agents.FindAsync(conversation.AgentId, includeDisabled: false, cancellationToken);
        return new NhAssistantConversationReadRule(
            conversation.Id,
            conversation.OwnerActorId,
            conversation.TenantId,
            null,
            agent is not null,
            agent?.Definition.RequiredPolicy,
            [conversation.TenantId])
        {
            ChatSubject = NhAssistantChatSubjects.SubjectOf(conversation)
        };
    }

    /// <summary>
    /// Whether <paramref name="actorId"/> may read the conversation now.
    /// </summary>
    public async ValueTask<bool> MayReadAsync(
        NhAssistantConversationReadRule rule,
        string actorId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (string.IsNullOrWhiteSpace(actorId)
            || actorId.StartsWith(NhAssistantAgentEvents.AgentActorPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        if (rule.Thread is { } thread)
        {
            return await threads.ReadAccessAsync(thread, actorId, cancellationToken) is not null;
        }

        var subject = ChatSubjectOf(rule);
        if (rule.ChatSubject is { } chatSubject)
        {
            // The owner too: a chat about a subject is read only while the subject's resolver allows it.
            var checkedSubject = await chatSubjects.CheckAsync(chatSubject, actorId, rule.TenantId, cancellationToken);
            if (!checkedSubject.Success)
            {
                return false;
            }

            subject = checkedSubject.Data!;
        }

        if (string.Equals(actorId, rule.OwnerActorId, StringComparison.Ordinal))
        {
            return true;
        }

        if (!rule.AgentWorks)
        {
            return false;
        }

        return rule.AgentPolicy is not { } policy
            || await recipients.PassesPolicyAsync(actorId, subject, policy, cancellationToken);
    }

    /// <summary>
    /// The people among <paramref name="actorIds"/> who may read the conversation now, each once, in
    /// their first order.
    /// </summary>
    public async Task<IReadOnlyList<string>> ReadersAsync(
        NhAssistantConversationReadRule rule,
        IEnumerable<string> actorIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(actorIds);
        var readers = new List<string>();
        foreach (var actorId in actorIds.Distinct(StringComparer.Ordinal))
        {
            if (await MayReadAsync(rule, actorId, cancellationToken))
            {
                readers.Add(actorId);
            }
        }

        return readers;
    }

    /// <summary>
    /// The people among <paramref name="actorIds"/> who may read the conversation now and whom the
    /// platform still resolves, each once, in their first order: the recipients of a notification about
    /// the conversation, which reaches people outside any session of theirs.
    /// </summary>
    public async Task<IReadOnlyList<string>> NotificationReadersAsync(
        NhAssistantConversationReadRule rule,
        IEnumerable<string> actorIds,
        CancellationToken cancellationToken)
    {
        var readers = await ReadersAsync(rule, actorIds, cancellationToken);
        return await ResolvedAsync(readers, cancellationToken);
    }

    /// <summary>
    /// The people among <paramref name="actorIds"/> whom the platform still resolves: not those whose
    /// account it deleted, blocked or locked out (<see cref="INhAssistantRecipientRules.IsResolvedAsync"/>).
    /// </summary>
    public async Task<IReadOnlyList<string>> ResolvedAsync(
        IEnumerable<string> actorIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actorIds);
        var resolved = new List<string>();
        foreach (var actorId in actorIds.Distinct(StringComparer.Ordinal))
        {
            if (await recipients.IsResolvedAsync(actorId, cancellationToken))
            {
                resolved.Add(actorId);
            }
        }

        return resolved;
    }

    /// <summary>
    /// The subject the principal factory rebuilds a chat's participant for: the whole application in the
    /// chat's tenant for a chat without a subject; a chat about one rebuilds them for that subject.
    /// </summary>
    private static NhAiSubjectInfo ChatSubjectOf(NhAssistantConversationReadRule rule)
    {
        return new NhAiSubjectInfo(NhAiSystemSubjects.Global, NhAiSubjectInfo.CreateScopeKey(rule.TenantId, null))
        {
            TenantId = rule.TenantId
        };
    }
}

/// <summary>
/// What the platform says about a person when the assistant delivers something to them without a
/// request of theirs: a live update, a Web Push notification or an in-app entry. The ASP.NET
/// integration answers with the application's principal factory, as headless runs rebuild a person;
/// the core library alone lets everyone pass.
/// </summary>
internal interface INhAssistantRecipientRules
{
    /// <summary>
    /// Whether the person, as the platform rebuilds them for <paramref name="subject"/>, passes
    /// <paramref name="policy"/>. <see langword="false"/> as well for a person the platform no longer
    /// resolves; <see langword="true"/> when the platform cannot tell.
    /// </summary>
    ValueTask<bool> PassesPolicyAsync(
        string actorId,
        NhAiSubjectInfo subject,
        string policy,
        CancellationToken cancellationToken);

    /// <summary>
    /// Whether the platform still resolves the person: <see langword="false"/> for an unknown person and
    /// one whose account it deleted, blocked or locked out, so that they may no longer sign in;
    /// <see langword="true"/> when the platform cannot tell.
    /// </summary>
    ValueTask<bool> IsResolvedAsync(string actorId, CancellationToken cancellationToken);
}

/// <summary>
/// The recipient rules without the ASP.NET integration: everyone passes.
/// </summary>
internal sealed class NhAssistantEveryoneReceives : INhAssistantRecipientRules
{
    public ValueTask<bool> PassesPolicyAsync(
        string actorId,
        NhAiSubjectInfo subject,
        string policy,
        CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(true);
    }

    public ValueTask<bool> IsResolvedAsync(string actorId, CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(true);
    }
}
