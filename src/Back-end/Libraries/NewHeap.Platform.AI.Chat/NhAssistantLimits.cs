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
    /// Maximum persisted messages replayed to the model as conversation history.
    /// </summary>
    public int MaxHistoryMessages { get; set; } = 40;

    /// <summary>
    /// Lifetime of an in-progress durable idempotency lease before another caller may take it over.
    /// </summary>
    public TimeSpan IdempotencyLeaseDuration { get; set; } = TimeSpan.FromMinutes(5);

    internal void Validate()
    {
        if (MaxToolCallsPerTurn < 1
            || MaxMessageChars < 1
            || MaxMessageChars > 64_000
            || TurnTimeout <= TimeSpan.Zero
            || DailyToolCallBudgetPerActor < 1
            || DailyModelCallBudgetPerActor is < 1
            || ApprovalLifetime <= TimeSpan.Zero
            || MaxToolsPerAgent < 1
            || MaxHistoryMessages < 1
            || IdempotencyLeaseDuration <= TimeSpan.Zero)
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
