using System.Text.Json;

namespace NewHeap.Platform.AI;

/// <summary>
/// An application object a proposal changes, such as <c>("office-task", "4711")</c>. The platform
/// indexes the keys of open proposals so the application can revalidate them by object.
/// </summary>
public sealed record NhAiProposalKey
{
    public NhAiProposalKey(string type, string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (type.Length > MaxTypeLength || id.Length > MaxIdLength)
        {
            throw new ArgumentException($"A proposal key type is at most {MaxTypeLength} and an id at most {MaxIdLength} characters.");
        }

        Type = type;
        Id = id;
    }

    public const int MaxTypeLength = 64;

    public const int MaxIdLength = 256;

    public string Type { get; }

    public string Id { get; }
}

/// <summary>When a proposal guard runs.</summary>
[Flags]
public enum NhAiProposalGuardStages
{
    None = 0,

    /// <summary>When the application revalidates open proposals of a subject or object.</summary>
    Revalidation = 1,

    /// <summary>When a person approves the proposal.</summary>
    Decision = 2,

    /// <summary>Right before an approved proposal executes.</summary>
    Execution = 4,

    /// <summary>The stages after a proposal was made. <see cref="Proposal"/> is declared on its own.</summary>
    All = Revalidation | Decision | Execution,

    /// <summary>
    /// When a rule or the model proposes, before the platform records the proposal. A stale result,
    /// such as <see cref="NhAiPreconditionResult.AlreadyDone"/>, records no proposal: the rule or the
    /// model learns why from the guard's explanation.
    /// </summary>
    Proposal = 8
}

/// <summary>
/// The facts a proposal guard works with. When a call proposes, at <see cref="NhAiProposalGuardStages.Proposal"/>
/// and for the keys of the new proposal (<see cref="INhAiProposalGuard.GetKeysAsync"/>), it also names the
/// call that proposes: <see cref="Invocation"/>, <see cref="ConversationId"/>, <see cref="TurnId"/> and
/// <see cref="Channel"/>, so a guard can refuse before an approval card is shown, for example a change
/// that a channel's chat may not propose. At revalidation, decision and execution no call proposes and
/// they are <see langword="null"/>, as they are for a proposal that a person leaves for the subject's
/// approvers: a guard must not authorize, or refuse, at those stages on them. None of them takes part in
/// the proposal hash or the approval evidence.
/// </summary>
/// <param name="ProposalId">The proposal.</param>
/// <param name="ToolId">The tool the proposal would run.</param>
/// <param name="Arguments">The exact arguments the proposal would run with.</param>
/// <param name="Subject">
/// The subject of the agent thread, or <see langword="null"/> for a proposal in a person's chat
/// outside a subject thread.
/// </param>
/// <param name="ScopeKey">
/// The isolation key of the subject, such as its division, or <see langword="null"/> without a
/// subject.
/// </param>
/// <param name="ProposedAt">When the proposal was made.</param>
public sealed record NhAiProposalGuardContext(
    Guid ProposalId,
    string ToolId,
    JsonElement Arguments,
    NhAiSubjectRef? Subject,
    string? ScopeKey,
    DateTimeOffset ProposedAt)
{
    /// <summary>
    /// The invocation context of the call that proposes, as the tool would run with it: its actor, scope,
    /// tenant, run, conversation and channel. Set only when a call proposes; <see langword="null"/> at
    /// the later stages.
    /// </summary>
    public NhAiInvocationContext? Invocation { get; init; }

    /// <summary>
    /// The assistant conversation of the call that proposes, as <see cref="NhAiInvocationContext.ConversationId"/>:
    /// the person's chat, the agent's thread or the consulted agent's thread. Set only when a call proposes;
    /// <see langword="null"/> at the later stages.
    /// </summary>
    public Guid? ConversationId { get; init; }

    /// <summary>
    /// The assistant turn of the call that proposes. Set only when a call proposes; <see langword="null"/>
    /// at the later stages.
    /// </summary>
    public Guid? TurnId { get; init; }

    /// <summary>
    /// The application's channel that started the turn of the call that proposes, as
    /// <see cref="NhAiInvocationContext.Channel"/>, such as <c>messaging-chat</c>; <see langword="null"/>
    /// for a turn of the assistant's own endpoints and an agent run, and at the later stages.
    /// </summary>
    public string? Channel { get; init; }
}

/// <summary>
/// An application check for proposals of one or more tools: which application objects a proposal
/// changes, and whether it still fits. Register it with <c>AddProposalGuard</c> on the assistant
/// builder for the tools it knows. Guards run without a signed-in person, must not change data,
/// and must be fast. A guard that throws never closes a proposal.
/// </summary>
public interface INhAiProposalGuard
{
    /// <summary>
    /// The application objects the proposal changes. Called once, when the proposal is made; the
    /// keys let the application revalidate the proposal when one of these objects changes.
    /// </summary>
    ValueTask<IReadOnlyList<NhAiProposalKey>> GetKeysAsync(
        NhAiProposalGuardContext context,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the proposal still fits, or, at <see cref="NhAiProposalGuardStages.Proposal"/>, whether
    /// it is needed at all. Return <see cref="NhAiPreconditionResult.Satisfied"/>, or a stale result
    /// such as <see cref="NhAiPreconditionResult.AlreadyDone"/> with an explanation.
    /// </summary>
    ValueTask<NhAiPreconditionResult> CheckAsync(
        NhAiProposalGuardContext context,
        CancellationToken cancellationToken = default);
}
