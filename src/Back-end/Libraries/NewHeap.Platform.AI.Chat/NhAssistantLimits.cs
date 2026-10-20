namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// Operational limits for assistant turns. Configure them through
/// <c>NhAssistantBuilder.WithLimits</c>.
/// </summary>
public sealed class NhAssistantLimits
{
    /// <summary>
    /// Maximum characters stored for tool arguments or results (64 KB).
    /// </summary>
    public const int MaxStoredJsonCharacters = 65_536;

    /// <summary>
    /// Maximum characters of an argument or result preview shown in the UI.
    /// </summary>
    public const int MaxPreviewCharacters = 2_000;

    /// <summary>
    /// Maximum characters of an agent's stored tool-selector list (its JSON array). This
    /// storage bound applies in addition to <see cref="MaxToolSelectorsPerAgent"/>.
    /// </summary>
    public const int MaxStoredToolSelectorCharacters = 8_000;

    /// <summary>
    /// Maximum characters of the text of one proposal feedback.
    /// </summary>
    public const int MaxFeedbackCharacters = 1_000;

    /// <summary>
    /// Maximum characters of the explanation of a stale proposal in one culture.
    /// </summary>
    public const int MaxStaleExplanationCharacters = 500;

    /// <summary>
    /// Maximum characters of the stored explanations of a stale proposal in all its cultures.
    /// </summary>
    public const int MaxStaleExplanationsSerializedLength = 6_000;

    /// <summary>
    /// Maximum application objects one proposal is indexed by.
    /// </summary>
    public const int MaxProposalKeys = 32;

    /// <summary>
    /// Maximum open proposals one revalidation checks; it checks the oldest first.
    /// </summary>
    public const int MaxRevalidatedProposals = 200;

    /// <summary>
    /// Maximum application objects one revalidation by key names.
    /// </summary>
    public const int MaxRevalidationKeys = 256;

    /// <summary>
    /// Maximum characters of the JSON payload a trigger stores with one event for rule handlers.
    /// </summary>
    public const int MaxInputPayloadCharacters = 4_000;

    /// <summary>
    /// The longest reason stored with a pause, stop or resume of an agent.
    /// </summary>
    public const int MaxStateReasonCharacters = 500;

    /// <summary>
    /// Maximum characters of the answer a run keeps as its result (K13); a longer answer keeps its
    /// beginning and is marked truncated. Files the run saved stay artifacts.
    /// </summary>
    public const int MaxRunResultCharacters = 20_000;

    /// <summary>
    /// Maximum governed tool calls in one turn. Further calls end the turn gracefully.
    /// </summary>
    public int MaxToolCallsPerTurn { get; set; } = 8;

    /// <summary>
    /// Maximum characters of one user message.
    /// </summary>
    public int MaxMessageChars { get; set; } = 8_000;

    /// <summary>
    /// Deadline of one turn, including model calls and tool execution.
    /// </summary>
    public TimeSpan TurnTimeout { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Durable daily tool-call budget per accountable human actor (UTC day).
    /// </summary>
    public int DailyToolCallBudgetPerActor { get; set; } = 200;

    /// <summary>
    /// Optional durable daily model-call budget per accountable human actor (UTC day).
    /// <see langword="null"/> leaves model calls bounded by the model profile only.
    /// </summary>
    public int? DailyModelCallBudgetPerActor { get; set; }

    /// <summary>
    /// Lifetime of a proposal that waits for a human decision.
    /// </summary>
    public TimeSpan ApprovalLifetime { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Maximum number of tools offered to one agent in one turn.
    /// </summary>
    public int MaxToolsPerAgent { get; set; } = 64;

    /// <summary>
    /// Maximum tool selectors of one agent, for code-defined and administrator-managed
    /// agents alike. Prefer prefix selectors such as <c>projects.*</c> over long lists of
    /// exact tool ids; the selectors must also fit
    /// <see cref="MaxStoredToolSelectorCharacters"/>. Raising it does not offer more tools
    /// per turn; <see cref="MaxToolsPerAgent"/> bounds that.
    /// </summary>
    public int MaxToolSelectorsPerAgent { get; set; } = 128;

    /// <summary>
    /// Maximum persisted messages replayed to the model as conversation history.
    /// </summary>
    public int MaxHistoryMessages { get; set; } = 40;

    /// <summary>
    /// How many summaries of earlier runs a new agent run starts with.
    /// </summary>
    public int MaxRunSummaries { get; set; } = 3;

    /// <summary>
    /// The largest artifact a tool may save, in bytes.
    /// </summary>
    public int MaxArtifactBytes { get; set; } = 1024 * 1024;

    /// <summary>
    /// Lifetime of an in-progress durable idempotency lease before another caller may take it over.
    /// </summary>
    public TimeSpan IdempotencyLeaseDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Maximum people the owner can share one conversation with, not counting the owner.
    /// </summary>
    public int MaxParticipantsPerConversation { get; set; } = 20;

    /// <summary>
    /// The longest lifetime any approval policy may give a proposal.
    /// </summary>
    public static TimeSpan MaxApprovalLifetime { get; } = TimeSpan.FromDays(30);

    /// <summary>
    /// How often the maintenance job expires, reminds and escalates proposals.
    /// </summary>
    public TimeSpan MaintenanceInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How deep consultations may nest: 1 lets an assistant consult an agent, while the consulted
    /// agent consults nobody. At most 3. An agent already in the chain is never consulted again.
    /// </summary>
    public int MaxConsultationDepth { get; set; } = 1;

    /// <summary>
    /// How many consultations one person may cause per UTC day, through any assistant.
    /// </summary>
    public int MaxConsultationsPerActorPerDay { get; set; } = 50;

    /// <summary>
    /// How many hand-offs a chain of agents may hold: 2 lets the agent that received work hand it on
    /// once more. At most 3. An agent already in the chain never receives work from it again.
    /// </summary>
    public int MaxHandOffDepth { get; set; } = 2;

    /// <summary>
    /// How many proposals one run may place at other objects than its own, such as a weekly run for
    /// the whole application that proposes to complete projects. Further ones are not placed and the
    /// model reads the reason <c>limit</c>; 0 places none. At most 200.
    /// </summary>
    public int MaxPlacedProposalsPerRun { get; set; } = 20;

    /// <summary>
    /// How many rejected proposals of its thread, with the feedback people gave, a new agent run
    /// starts with, so it does not propose them again unchanged. 0 leaves them out. At most 20.
    /// </summary>
    public int MaxRejectionsInContext { get; set; } = 5;

    /// <summary>
    /// How far back a new agent run sees rejected proposals of its thread.
    /// </summary>
    public TimeSpan RejectionContextWindow { get; set; } = TimeSpan.FromDays(30);

    /// <summary>
    /// The characters of titles and texts a subject briefing gives a model turn, at most 32,000.
    /// Sections beyond it are left out with a note. The briefing counts against the turn's history.
    /// </summary>
    public int MaxBriefingCharacters { get; set; } = 8_000;

    /// <summary>
    /// How long a subject briefing provider may take; a slower one leaves the turn without a briefing.
    /// </summary>
    public TimeSpan BriefingTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The characters of the application map a turn reads, at most 16,000. Resources beyond it are
    /// left out with a note to use <c>search-resources</c>.
    /// </summary>
    public int MaxApplicationMapCharacters { get; set; } = 4_000;

    /// <summary>
    /// How long the application map sources may take together; slower ones leave the turn without
    /// the map.
    /// </summary>
    public TimeSpan ApplicationMapTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The most characters of retrieved context a turn reads, at most 24,000; an agent's own bound
    /// is cut to it.
    /// </summary>
    public int MaxRetrievedCharacters { get; set; } = 6_000;

    /// <summary>
    /// How long retrieval may take; a slower resolver leaves the turn without retrieved context.
    /// </summary>
    public TimeSpan RetrievalTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a board remembers when a person last looked at it, from one day to ten years; by
    /// default 180 days. The run maintenance removes older visits, and visits of groups the host no
    /// longer registers; a board then shows everything that waits as new again.
    /// </summary>
    public TimeSpan BoardVisitRetention { get; set; } = TimeSpan.FromDays(180);

    internal void Validate()
    {
        if (MaintenanceInterval < TimeSpan.FromSeconds(1) || MaintenanceInterval > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(MaintenanceInterval), "The maintenance interval is between one second and one hour.");
        }

        if (MaxToolCallsPerTurn < 1
            || MaxMessageChars < 1
            || MaxMessageChars > 64_000
            || TurnTimeout <= TimeSpan.Zero
            || DailyToolCallBudgetPerActor < 1
            || DailyModelCallBudgetPerActor is < 1
            || ApprovalLifetime <= TimeSpan.Zero
            || MaxToolsPerAgent < 1
            || MaxToolSelectorsPerAgent < 1
            || MaxHistoryMessages < 1
            || MaxRunSummaries is < 0 or > 20
            || MaxArtifactBytes is < 1 or > 16 * 1024 * 1024
            || IdempotencyLeaseDuration <= TimeSpan.Zero
            || MaxParticipantsPerConversation is < 1 or > 100
            || MaxConsultationDepth is < 1 or > 3
            || MaxConsultationsPerActorPerDay < 1
            || MaxHandOffDepth is < 1 or > 3
            || MaxPlacedProposalsPerRun is < 0 or > 200
            || MaxRejectionsInContext is < 0 or > 20
            || RejectionContextWindow < TimeSpan.FromDays(1)
            || RejectionContextWindow > TimeSpan.FromDays(365)
            || MaxBriefingCharacters is < 1 or > 32_000
            || BriefingTimeout <= TimeSpan.Zero
            || BriefingTimeout > TimeSpan.FromMinutes(1)
            || MaxApplicationMapCharacters is < 500 or > 16_000
            || ApplicationMapTimeout <= TimeSpan.Zero
            || ApplicationMapTimeout > TimeSpan.FromSeconds(30)
            || MaxRetrievedCharacters is < 500 or > 24_000
            || RetrievalTimeout <= TimeSpan.Zero
            || RetrievalTimeout > TimeSpan.FromSeconds(30)
            || BoardVisitRetention < TimeSpan.FromDays(1)
            || BoardVisitRetention > TimeSpan.FromDays(3_650))
        {
            throw new InvalidOperationException("Assistant limits must be positive and bounded.");
        }
    }

    internal NhAssistantLimits Clone()
    {
        return (NhAssistantLimits)MemberwiseClone();
    }
}

/// <summary>
/// Storage options for the library-owned assistant schema.
/// </summary>
public sealed class NhAssistantDbContextOptions
{
    public const string DefaultSchema = "nhai";

    /// <summary>
    /// Database schema that owns the assistant tables. Defaults to <c>nhai</c>.
    /// </summary>
    public string Schema { get; set; } = DefaultSchema;

    /// <summary>
    /// Applies pending library migrations while the host starts. Off by default: applying
    /// migrations is an explicit deployment decision.
    /// </summary>
    public bool RunMigrations { get; set; }
}
