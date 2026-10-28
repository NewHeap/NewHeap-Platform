namespace NewHeap.Platform.AI.Scripting;

/// <summary>
/// The hard limits of one script. Every script gets a fresh engine with these limits; a script that
/// reaches one stops with its code (<see cref="NhAiScriptCodes"/>) and the result still lists what its
/// calls did. The defaults suit a short program of a few reads; an agent or an MCP exposure that opts in
/// may set its own.
/// </summary>
public sealed class NhAiScriptLimits
{
    /// <summary>
    /// The longest program the script tool accepts at all. The assistant stores the call's arguments up to
    /// 64 KB of JSON, and a program of this length still fits when every character is escaped, so the
    /// activity always shows the whole script.
    /// </summary>
    public const int HardMaxScriptLength = 10_000;

    /// <summary>The most tool calls one script may make at all.</summary>
    public const int HardMaxToolCalls = 64;

    /// <summary>
    /// The longest program, in characters. At most <see cref="HardMaxScriptLength"/>. Default 8,000.
    /// </summary>
    public int MaxScriptLength { get; set; } = 8_000;

    /// <summary>
    /// How long a script may run, its tool calls included. A call that has started finishes, and the script
    /// stops before the next statement or call. Default 30 seconds; at most 10 minutes.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The most statements a script runs, counted over the whole script. Default 100,000.
    /// </summary>
    public int MaxStatements { get; set; } = 100_000;

    /// <summary>
    /// The most memory a script allocates, in bytes, counted over the whole script. Default 16 MiB; at
    /// least 1 MiB.
    /// </summary>
    public long MaxMemoryBytes { get; set; } = 16 * 1024 * 1024;

    /// <summary>
    /// How often one function may be on the call stack at once. Default 64.
    /// </summary>
    public int MaxRecursionDepth { get; set; } = 64;

    /// <summary>
    /// The most tool calls a script makes; the call after the last one does not run. Default 20; at most
    /// <see cref="HardMaxToolCalls"/>, the most steps the assistant's step runtime runs for one call.
    /// </summary>
    public int MaxToolCalls { get; set; } = 20;

    /// <summary>
    /// The most calls of tools that change something (every effect other than
    /// <see cref="NhAiToolEffect.ReadOnly"/>) a script makes, counted whether the call ran, was proposed or
    /// failed. <c>0</c>, the default, makes scripts read-only: they are not offered such tools at all. Each
    /// such call still gets exactly the decision the same call gets on its own.
    /// </summary>
    public int MaxMutationsPerScript { get; set; }

    /// <summary>
    /// The largest value a script may return, in bytes of UTF-8 JSON. Only this value returns to the
    /// model. Default 16 KiB.
    /// </summary>
    public int MaxResultBytes { get; set; } = 16 * 1024;

    /// <summary>
    /// The longest description of the script tool, in characters, with the signatures of its tools. Beyond
    /// it, tools are listed with their argument names only and then by name; a failing call always returns
    /// the exact signature. Default 16,000.
    /// </summary>
    public int MaxDescriptionLength { get; set; } = 16_000;

    /// <summary>
    /// A copy of these limits.
    /// </summary>
    public NhAiScriptLimits Clone()
    {
        return new NhAiScriptLimits
        {
            MaxScriptLength = MaxScriptLength,
            Timeout = Timeout,
            MaxStatements = MaxStatements,
            MaxMemoryBytes = MaxMemoryBytes,
            MaxRecursionDepth = MaxRecursionDepth,
            MaxToolCalls = MaxToolCalls,
            MaxMutationsPerScript = MaxMutationsPerScript,
            MaxResultBytes = MaxResultBytes,
            MaxDescriptionLength = MaxDescriptionLength
        };
    }

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> when a limit is outside its range.
    /// </summary>
    public void Validate()
    {
        Require(MaxScriptLength is >= 1 and <= HardMaxScriptLength, $"MaxScriptLength is between 1 and {HardMaxScriptLength} characters.");
        Require(Timeout > TimeSpan.Zero && Timeout <= TimeSpan.FromMinutes(10), "Timeout is more than zero and at most 10 minutes.");
        Require(MaxStatements >= 1, "MaxStatements is at least 1.");
        Require(MaxMemoryBytes >= 1024 * 1024, "MaxMemoryBytes is at least 1 MiB.");
        Require(MaxRecursionDepth is >= 1 and <= 1_000, "MaxRecursionDepth is between 1 and 1,000.");
        Require(MaxToolCalls is >= 1 and <= HardMaxToolCalls, $"MaxToolCalls is between 1 and {HardMaxToolCalls}.");
        Require(MaxMutationsPerScript >= 0 && MaxMutationsPerScript <= MaxToolCalls, "MaxMutationsPerScript is between 0 and MaxToolCalls.");
        Require(MaxResultBytes is >= 256 and <= 1024 * 1024, "MaxResultBytes is between 256 bytes and 1 MiB.");
        Require(MaxDescriptionLength is >= 2_000 and <= 100_000, "MaxDescriptionLength is between 2,000 and 100,000 characters.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Script limits: " + message);
        }
    }
}
