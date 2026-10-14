namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// Closes open agent proposals that no longer fit, for example because a person did the work by
/// hand. It runs the proposal guards registered with <c>AddProposalGuard</c> for the open proposals
/// of agent runs and consultations; a guard that reports stale closes its proposal live, with its
/// explanation. Call it after the application's change committed. Repeating a call changes nothing.
/// </summary>
public interface INhAssistantProposalRevalidation
{
    /// <summary>
    /// Revalidates the open proposals of every agent thread about <paramref name="subject"/>.
    /// </summary>
    Task<NhAssistantRevalidationResult> RevalidateSubjectAsync(
        NhAiSubjectRef subject,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Revalidates the open proposals that change one of <paramref name="keys"/>, as their guards
    /// named them, in any subject's thread within <paramref name="scopeKey"/>. Without a scope key
    /// the scope of subjects without tenant or division (<c>global</c>) is used.
    /// </summary>
    Task<NhAssistantRevalidationResult> RevalidateKeysAsync(
        IReadOnlyCollection<NhAiProposalKey> keys,
        string? scopeKey = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// What a revalidation did.
/// </summary>
/// <param name="Invalidated">The proposals it closed as stale.</param>
/// <param name="Checked">The open proposals whose guards ran.</param>
/// <param name="Failed">The proposals a guard failed to check; they stay open.</param>
public sealed record NhAssistantRevalidationResult(
    IReadOnlyList<NhAssistantInvalidatedProposal> Invalidated,
    int Checked,
    int Failed)
{
    public static NhAssistantRevalidationResult Empty { get; } = new([], 0, 0);
}

/// <summary>
/// A proposal a revalidation closed as stale.
/// </summary>
/// <param name="Kind">One of <see cref="NhAssistantStaleKinds"/>.</param>
/// <param name="ReasonCode">The guard's reason code.</param>
public sealed record NhAssistantInvalidatedProposal(
    Guid ApprovalId,
    Guid ConversationId,
    string ToolId,
    string Kind,
    string ReasonCode);

/// <summary>
/// Configures a declarative revalidation; see
/// <see cref="NhAssistantBuilder.AddProposalRevalidation{TEvent}"/>.
/// </summary>
public sealed class NhAssistantRevalidationBuilder<TEvent>
    where TEvent : class
{
    private Func<TEvent, NhAiSubjectRef?>? _subject;
    private Func<TEvent, IEnumerable<NhAiProposalKey>>? _keys;
    private Func<TEvent, string?> _scopeKey = _ => null;

    internal NhAssistantRevalidationBuilder()
    {
    }

    /// <summary>
    /// The subject whose open proposals the event revalidates, or <see langword="null"/> for none.
    /// </summary>
    public NhAssistantRevalidationBuilder<TEvent> Subject(Func<TEvent, NhAiSubjectRef?> subject)
    {
        _subject = subject ?? throw new ArgumentNullException(nameof(subject));
        return this;
    }

    /// <summary>
    /// The application objects the event changed; open proposals that change one of them are
    /// revalidated.
    /// </summary>
    public NhAssistantRevalidationBuilder<TEvent> Keys(Func<TEvent, IEnumerable<NhAiProposalKey>> keys)
    {
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        return this;
    }

    /// <summary>
    /// The scope key of the objects named by <see cref="Keys"/>, such as
    /// <c>NhAiSubjectInfo.CreateScopeKey(null, evt.DivisionId)</c>.
    /// </summary>
    public NhAssistantRevalidationBuilder<TEvent> ScopeKey(Func<TEvent, string?> scopeKey)
    {
        _scopeKey = scopeKey ?? throw new ArgumentNullException(nameof(scopeKey));
        return this;
    }

    internal NhAssistantRevalidationDefinition Build()
    {
        if (_subject is null && _keys is null)
        {
            throw new InvalidOperationException(
                $"The proposal revalidation of {typeof(TEvent).Name} needs Subject or Keys.");
        }

        var subject = _subject;
        var keys = _keys;
        var scopeKey = _scopeKey;
        return new NhAssistantRevalidationDefinition(
            typeof(TEvent),
            subject is null ? null : evt => subject((TEvent)evt),
            keys is null ? null : evt => keys((TEvent)evt),
            evt => scopeKey((TEvent)evt));
    }
}

/// <summary>
/// A declarative revalidation: which subject and objects an event of <see cref="EventType"/> changed.
/// </summary>
internal sealed record NhAssistantRevalidationDefinition(
    Type EventType,
    Func<object, NhAiSubjectRef?>? Subject,
    Func<object, IEnumerable<NhAiProposalKey>>? Keys,
    Func<object, string?> ScopeKey);
