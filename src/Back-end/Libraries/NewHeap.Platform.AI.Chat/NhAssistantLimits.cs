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
    /// How many rejected proposals of its thread, with the feedback people gave, a new agent run
    /// starts with, so it does not propose them again unchanged. 0 leaves them out. At most 20.
    /// </summary>
    public int MaxRejectionsInContext { get; set; } = 5;

    /// <summary>
    /// How far back a new agent run sees rejected proposals of its thread.
    /// </summary>
    public TimeSpan RejectionContextWindow { get; set; } = TimeSpan.FromDays(30);

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
            || MaxRejectionsInContext is < 0 or > 20
            || RejectionContextWindow < TimeSpan.FromDays(1)
            || RejectionContextWindow > TimeSpan.FromDays(365))
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
