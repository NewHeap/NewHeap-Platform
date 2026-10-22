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

/// <summary>The facts a proposal guard works with.</summary>
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
    DateTimeOffset ProposedAt);

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
