using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;

namespace NewHeap.Platform.AI.Chat.Collaboration;

/// <summary>
/// How far a person may follow a conversation at the moment the assistant delivers something about it.
/// </summary>
internal enum NhAssistantReaderStanding
{
    /// <summary>The person may not read the conversation.</summary>
    None = 0,

    /// <summary>
    /// Content-free changes of an agent thread's runs, proposals and feedback: the thread's read rule
    /// admits the person, as the thread endpoints do, but the agent's policy keeps its messages from
    /// them.
    /// </summary>
    Work = 1,

    /// <summary>The conversation's content, as its snapshot shows it to the person.</summary>
    Content = 2
}

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
    IReadOnlyList<string?> Tenants);

/// <summary>
/// Decides who may follow a conversation when something is delivered to them without a request of
/// theirs: live updates, Web Push and in-app notifications. It applies, at send time, the rule the
/// snapshot endpoints apply on every request. An agent thread follows its subject's resolver: a live
/// role, or an invitation that is no subject join, and for the whole application the agent's view
/// policy (<see cref="NhAssistantThreadService.ReadAccessAsync(AssistantThread, string, CancellationToken)"/>);
/// its messages also need the agent's policy, as <c>GET conversations/{id}</c> asks. A chat's owner
/// always reads it; a participant reads it while the agent works and the person passes its policy, as
/// the principal factory rebuilds them. Agents never receive anything. The tenant rule binds the
/// delivery to the sign-ins of <see cref="NhAssistantConversationReadRule.Tenants"/> where a channel
/// knows the sign-in, as the live hub does.
/// </summary>
internal sealed class NhAssistantConversationReaders(
    NhAssistantRunStore runs,
    NhAssistantThreadService threads,
    NhAssistantAgentCatalog agents,
    NhAssistantRegistrationState registration,
    INhAssistantRecipientRules recipients)
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
        var agent = await agents.FindAsync(conversation.AgentId, includeDisabled: false, cancellationToken);
        IReadOnlyList<string?> tenants = thread is not null
            && thread.TenantId is null
            && string.Equals(thread.SubjectType, NhAiSubjectRef.SystemType, StringComparison.Ordinal)
            && registration.SingleTenantId is { } single
                ? [null, single]
                : [thread is null ? conversation.TenantId : thread.TenantId];
        return new NhAssistantConversationReadRule(
            conversation.Id,
            conversation.OwnerActorId,
            conversation.TenantId,
            thread,
            agent is not null,
            agent?.Definition.RequiredPolicy,
            tenants);
    }

    /// <summary>
    /// How far <paramref name="actorId"/> may follow the conversation now.
    /// </summary>
    public async ValueTask<NhAssistantReaderStanding> StandingAsync(
        NhAssistantConversationReadRule rule,
        string actorId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (string.IsNullOrWhiteSpace(actorId)
            || actorId.StartsWith(NhAssistantAgentEvents.AgentActorPrefix, StringComparison.Ordinal))
        {
            return NhAssistantReaderStanding.None;
        }

        if (rule.Thread is { } thread)
        {
            if (await threads.ReadAccessAsync(thread, actorId, cancellationToken) is null)
            {
                return NhAssistantReaderStanding.None;
            }

            return await PassesAgentAsync(rule, actorId, cancellationToken)
                ? NhAssistantReaderStanding.Content
                : NhAssistantReaderStanding.Work;
        }

        if (string.Equals(actorId, rule.OwnerActorId, StringComparison.Ordinal))
        {
            return NhAssistantReaderStanding.Content;
        }

        return await PassesAgentAsync(rule, actorId, cancellationToken)
            ? NhAssistantReaderStanding.Content
            : NhAssistantReaderStanding.None;
    }

    /// <summary>
    /// The people among <paramref name="actorIds"/> who may read the conversation's content now, each
    /// once, in their first order.
    /// </summary>
    public async Task<IReadOnlyList<string>> ContentReadersAsync(
        NhAssistantConversationReadRule rule,
        IEnumerable<string> actorIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(actorIds);
        var readers = new List<string>();
        foreach (var actorId in actorIds.Distinct(StringComparer.Ordinal))
        {
            if (await StandingAsync(rule, actorId, cancellationToken) == NhAssistantReaderStanding.Content)
            {
                readers.Add(actorId);
            }
        }

        return readers;
    }

    /// <summary>
    /// The people among <paramref name="actorIds"/> who may read the conversation's content now and
    /// whom the platform still resolves, each once, in their first order: the recipients of a
    /// notification about the conversation, which reaches people outside any session of theirs.
    /// </summary>
    public async Task<IReadOnlyList<string>> NotificationReadersAsync(
        NhAssistantConversationReadRule rule,
        IEnumerable<string> actorIds,
        CancellationToken cancellationToken)
    {
        var readers = await ContentReadersAsync(rule, actorIds, cancellationToken);
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
    /// Whether the agent works and the person passes its required policy.
    /// </summary>
    private async ValueTask<bool> PassesAgentAsync(
        NhAssistantConversationReadRule rule,
        string actorId,
        CancellationToken cancellationToken)
    {
        if (!rule.AgentWorks)
        {
            return false;
        }

        return rule.AgentPolicy is not { } policy
            || await recipients.PassesPolicyAsync(actorId, SubjectOf(rule), policy, cancellationToken);
    }

    /// <summary>
    /// The subject the principal factory rebuilds a person for: the thread's subject, or for a chat the
    /// whole application in the chat's tenant.
    /// </summary>
    private static NhAiSubjectInfo SubjectOf(NhAssistantConversationReadRule rule)
    {
        if (rule.Thread is { } thread)
        {
            return new NhAiSubjectInfo(new NhAiSubjectRef(thread.SubjectType, thread.SubjectId), thread.ScopeKey)
            {
                TenantId = thread.TenantId,
                DivisionId = thread.DivisionId,
                DisplayName = thread.SubjectDisplayName
            };
        }

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
