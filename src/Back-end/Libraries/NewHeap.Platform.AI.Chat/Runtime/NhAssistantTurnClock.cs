using System.Diagnostics;

namespace NewHeap.Platform.AI.Chat.Runtime;

/// <summary>
/// Adds up where the time of one turn went, on the system clock, since a duration is no business
/// deadline. Tools and model calls report from the turn's own flow, and a step of a composition from
/// the call around it, so every value is written with <see cref="Interlocked"/>.
/// </summary>
internal sealed class NhAssistantTurnClock
{
    private readonly long _startedAt = Stopwatch.GetTimestamp();
    private long _firstModelCallAt;
    private long _firstTextAt;
    private long _mcpToolsTicks;
    private long _retrievalTicks;
    private long _filesTicks;
    private long _modelTicks;
    private long _toolsTicks;
    private int _modelCalls;

    /// <summary>
    /// Marks the end of the turn's preparation, once: its first model call starts now.
    /// </summary>
    public void ModelStarting()
    {
        Interlocked.CompareExchange(ref _firstModelCallAt, Stopwatch.GetTimestamp(), 0);
    }

    /// <summary>
    /// Marks the moment a person first has something to read, once.
    /// </summary>
    public void TextReceived()
    {
        Interlocked.CompareExchange(ref _firstTextAt, Stopwatch.GetTimestamp(), 0);
    }

    public void AddMcpTools(TimeSpan elapsed)
    {
        Interlocked.Add(ref _mcpToolsTicks, elapsed.Ticks);
    }

    public void AddRetrieval(TimeSpan elapsed)
    {
        Interlocked.Add(ref _retrievalTicks, elapsed.Ticks);
    }

    public void AddFiles(TimeSpan elapsed)
    {
        Interlocked.Add(ref _filesTicks, elapsed.Ticks);
    }

    public void AddTool(TimeSpan elapsed)
    {
        Interlocked.Add(ref _toolsTicks, elapsed.Ticks);
    }

    public void AddModelCall(TimeSpan elapsed)
    {
        Interlocked.Add(ref _modelTicks, elapsed.Ticks);
        Interlocked.Increment(ref _modelCalls);
    }

    /// <summary>
    /// The turn's timings so far. A turn that never reached its model spent all its time preparing.
    /// </summary>
    public NhAssistantTurnTimings Snapshot()
    {
        var firstModelCallAt = Interlocked.Read(ref _firstModelCallAt);
        var firstTextAt = Interlocked.Read(ref _firstTextAt);
        var total = Stopwatch.GetElapsedTime(_startedAt);
        return new NhAssistantTurnTimings(
            total,
            firstModelCallAt == 0 ? total : Stopwatch.GetElapsedTime(_startedAt, firstModelCallAt),
            TimeSpan.FromTicks(Interlocked.Read(ref _modelTicks)),
            Volatile.Read(ref _modelCalls),
            TimeSpan.FromTicks(Interlocked.Read(ref _toolsTicks)))
        {
            McpTools = TimeSpan.FromTicks(Interlocked.Read(ref _mcpToolsTicks)),
            Retrieval = TimeSpan.FromTicks(Interlocked.Read(ref _retrievalTicks)),
            Files = TimeSpan.FromTicks(Interlocked.Read(ref _filesTicks)),
            FirstText = firstTextAt == 0 ? null : Stopwatch.GetElapsedTime(_startedAt, firstTextAt)
        };
    }
}

/// <summary>
/// Adds the model calls of a turn to its clock: the executor records every governed call here, on the
/// turn's own flow, whichever profile of a fallback chain answered it.
/// </summary>
internal sealed class NhAssistantTurnModelCalls : INhAiUsageSink
{
    public ValueTask WriteAsync(NhAiUsageRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        NhAssistantExecutionScope.CurrentTurn?.Clock.AddModelCall(record.Duration);
        return ValueTask.CompletedTask;
    }
}
