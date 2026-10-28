namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// Supplies a compact sketch of a subject at the start of every model turn of an agent about it:
/// headless runs, the model part of a rule run that hands over, people's turns in the subject's
/// thread, consultations and people's messages in their own chat about the subject (FB25). Register one
/// provider per subject type with
/// <c>NhAssistantBuilder.AddSubjectBriefing</c>. It runs in the turn's service scope, without a
/// person for headless runs, and reads the application's own data directly, like a rule handler.
/// State facts with their dates and leave judgement to the model; a briefing reaches the model
/// provider like a tool result, so follow the agent's data classification.
/// </summary>
public interface INhAssistantSubjectBriefingProvider
{
    /// <summary>
    /// The subject type it briefs, as registered with <c>AddSubjectType</c>.
    /// </summary>
    string SubjectType { get; }

    /// <summary>
    /// The briefing of the subject, or <see langword="null"/> for none. A provider that throws or
    /// takes longer than <see cref="NhAssistantLimits.BriefingTimeout"/> leaves the turn without a
    /// briefing; the turn itself goes on.
    /// </summary>
    ValueTask<NhAssistantSubjectBriefing?> GetBriefingAsync(
        NhAssistantBriefingContext context,
        CancellationToken cancellationToken);
}

/// <param name="AgentId">The agent whose turn is briefed.</param>
/// <param name="Subject">The subject, as its access resolver described it.</param>
/// <param name="Purpose">One of <see cref="NhAssistantBriefingPurposes"/>.</param>
/// <param name="Inputs">The events of a headless run, oldest first; empty for people's turns and
/// consultations.</param>
public sealed record NhAssistantBriefingContext(
    string AgentId,
    NhAiSubjectInfo Subject,
    string Purpose,
    IReadOnlyList<NhAssistantRuleInput> Inputs);

/// <summary>
/// Why a turn is briefed.
/// </summary>
public static class NhAssistantBriefingPurposes
{
    /// <summary>A headless run of the agent, including the model part of a rule run.</summary>
    public const string Run = "run";

    /// <summary>A person's turn in the subject's thread.</summary>
    public const string Thread = "thread";

    /// <summary>Another assistant consults the agent about the subject.</summary>
    public const string Consultation = "consultation";

    /// <summary>
    /// A person's message in their own chat about the subject (FB25). The turn runs with that person's
    /// rights and the chat is theirs, not the agent's thread, so brief only what the person may read.
    /// </summary>
    public const string Chat = "chat";
}

/// <summary>
/// The sections of a subject briefing, in the order the model reads them. The briefing is bounded
/// by <see cref="NhAssistantLimits.MaxBriefingCharacters"/>: sections are kept in order until the
/// bound is reached and the rest are left out with a note, so put what matters most first.
/// </summary>
public sealed record NhAssistantSubjectBriefing(IReadOnlyList<NhAssistantBriefingSection> Sections);

/// <param name="Title">A short title, at most 80 characters; a longer one is cut.</param>
/// <param name="Text">The facts, as plain text; markup characters are neutralized.</param>
public sealed record NhAssistantBriefingSection(string Title, string Text)
{
    public const int MaxTitleLength = 80;
}
