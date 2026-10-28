namespace NewHeap.Platform.AI;

/// <summary>
/// What made a governed call when a model did not choose it itself.
/// </summary>
public enum NhAiToolCallOriginKind
{
    /// <summary>A step of a composition: a chain of tools that an application declared as one tool.</summary>
    Composition = 1,

    /// <summary>A call of a script that a model wrote and the script tool ran on the server.</summary>
    Script = 2
}

/// <summary>
/// The composition or script a governed call is a step of. The step runtime of the assistant sets it
/// for every step it runs; the invoker puts it on <see cref="NhAiInvocationContext.Origin"/> and on the
/// call's <see cref="NhAiAuditRecord.Origin"/>, and the assistant on
/// <see cref="NhAiProposalGuardContext.Origin"/> of a proposal the step makes. A call the model, a
/// person or the application made directly has none. It is content-free: it names the composition or
/// script tool, the script's hash, the call it is a step of and the step's position, never arguments
/// or results. It takes part in no proposal hash or approval evidence.
/// </summary>
/// <param name="Kind">Whether a composition or a script made the call.</param>
/// <param name="ToolId">The id of the composition, or of the script tool, whose call ran the step.</param>
/// <param name="Hash">
/// For a script, the SHA-256 of the script text as lowercase hexadecimal; for a composition, the contract
/// hash of the composition tool. At most <see cref="MaxHashLength"/> characters.
/// </param>
public sealed record NhAiToolCallOrigin
{
    /// <summary>The longest <see cref="Hash"/>.</summary>
    public const int MaxHashLength = 128;

    public NhAiToolCallOrigin(NhAiToolCallOriginKind kind, string toolId, string? hash = null)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
        if (toolId.Length > 256 || toolId.Trim() != toolId)
        {
            throw new ArgumentException("The tool id of a call's origin is at most 256 characters without surrounding whitespace.", nameof(toolId));
        }

        if (hash is not null && (hash.Length is 0 or > MaxHashLength || !hash.All(char.IsAsciiLetterOrDigit)))
        {
            throw new ArgumentException($"The hash of a call's origin is at most {MaxHashLength} ASCII letters or digits.", nameof(hash));
        }

        Kind = kind;
        ToolId = toolId;
        Hash = hash;
    }

    /// <summary>Whether a composition or a script made the call.</summary>
    public NhAiToolCallOriginKind Kind { get; }

    /// <summary>The id of the composition, or of the script tool, whose call ran the step.</summary>
    public string ToolId { get; }

    /// <summary>The script's hash, or the composition's contract hash.</summary>
    public string? Hash { get; }

    /// <summary>
    /// The invocation id of the composition or script call this call is a step of: in an assistant turn
    /// the id of that call's tool invocation, otherwise the id of its invocation context.
    /// </summary>
    public Guid? ParentInvocationId { get; init; }

    /// <summary>The position of the step in its composition or script, from 0.</summary>
    public int? StepIndex { get; init; }
}

/// <summary>
/// Carries the origin of the next governed call in the current asynchronous flow from the step runtime
/// to the invoker. The invoker takes it once, so a call that the step's tool makes internally is not
/// marked as a step.
/// </summary>
internal static class NhAiToolCallOrigins
{
    private static readonly AsyncLocal<NhAiToolCallOrigin?> CurrentOrigin = new();

    public static NhAiToolCallOrigin? Current => CurrentOrigin.Value;

    /// <summary>
    /// Marks the governed call that the calling flow makes next as a step of <paramref name="origin"/>.
    /// </summary>
    public static IDisposable Begin(NhAiToolCallOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(origin);
        var previous = CurrentOrigin.Value;
        CurrentOrigin.Value = origin;
        return new Restore(previous);
    }

    /// <summary>
    /// Returns the origin of the call that starts now and clears it for everything that call awaits.
    /// Call it from the invoker's own asynchronous method, so the caller's flow keeps its value.
    /// </summary>
    public static NhAiToolCallOrigin? Take()
    {
        var origin = CurrentOrigin.Value;
        if (origin is not null)
        {
            CurrentOrigin.Value = null;
        }

        return origin;
    }

    private sealed class Restore(NhAiToolCallOrigin? previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                CurrentOrigin.Value = previous;
            }
        }
    }
}
