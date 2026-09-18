using System.Collections.Concurrent;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI;

/// <summary>
/// Process-local per-actor budget limits. State is not durable and resets when the process
/// restarts; use a durable <see cref="INhAiBudgetManager"/> for production guarantees.
/// </summary>
public sealed class NhAiInMemoryBudgetOptions
{
    public int MaxCalls { get; set; } = 100;

    public int MaxInputTokens { get; set; } = 1_000_000;

    public int MaxOutputTokens { get; set; } = 250_000;

    public decimal? MaxEstimatedCost { get; set; }

    public TimeSpan Window { get; set; } = TimeSpan.FromDays(1);

    /// <summary>Upper bound of actor/window ledgers retained by this process.</summary>
    public int MaxActorWindows { get; set; } = 10_000;
}

/// <summary>A bounded, process-local per-actor budget manager for samples and local use.</summary>
public sealed class NhAiInMemoryBudgetManager : INhAiBudgetManager
{
    public const string ExhaustedCode = "ai-budget-exhausted";
    public const string ActorRequiredCode = "ai-budget-actor-required";

    private readonly NhAiInMemoryBudgetOptions _options;
    private readonly ConcurrentDictionary<(string ActorId, long Window), Entry> _entries = new();

    public NhAiInMemoryBudgetManager(NhAiInMemoryBudgetOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Validate(options);
        _options = options;
    }

    public ValueTask<TaskResult<NhAiBudgetReservation>> ReserveAsync(
        NhAiBudgetRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(request.ActorId))
        {
            return Failed(ActorRequiredCode, "An authenticated actor is required for the AI budget.");
        }
        if (request.RequestedCalls < 0
            || request.RequestedInputTokens < 0
            || request.RequestedOutputTokens < 0
            || request.RequestedEstimatedCost < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }

        var now = DateTimeOffset.UtcNow;
        var windowTicks = _options.Window.Ticks;
        var window = now.UtcTicks / windowTicks;
        var expiresAt = new DateTimeOffset((window + 1) * windowTicks, TimeSpan.Zero);
        var key = (request.ActorId, window);
        if (!_entries.TryGetValue(key, out var entry))
        {
            TrimExpired(window);
            if (_entries.Count >= _options.MaxActorWindows)
            {
                return Failed(ExhaustedCode, "The process-local AI budget ledger is full.");
            }
            entry = _entries.GetOrAdd(key, _ => new Entry());
        }

        lock (entry.Sync)
        {
            if (WouldExceed(entry, request))
            {
                return Failed(ExhaustedCode, "The AI budget for this actor is exhausted.");
            }
            entry.Calls += request.RequestedCalls;
            entry.InputTokens += request.RequestedInputTokens;
            entry.OutputTokens += request.RequestedOutputTokens;
            entry.EstimatedCost += request.RequestedEstimatedCost ?? 0;

            var remaining = new NhAiModelBudget(
                _options.MaxInputTokens - entry.InputTokens,
                _options.MaxOutputTokens - entry.OutputTokens,
                _options.MaxCalls - entry.Calls,
                _options.MaxEstimatedCost is null
                    ? null
                    : _options.MaxEstimatedCost.Value - entry.EstimatedCost);
            return ValueTask.FromResult(TaskResult<NhAiBudgetReservation>.Succeeded(
                new NhAiBudgetReservation(
                    request.InvocationId.ToString("N"),
                    remaining,
                    expiresAt)));
        }
    }

    private bool WouldExceed(Entry entry, NhAiBudgetRequest request)
    {
        return entry.Calls + request.RequestedCalls > _options.MaxCalls
            || entry.InputTokens + request.RequestedInputTokens > _options.MaxInputTokens
            || entry.OutputTokens + request.RequestedOutputTokens > _options.MaxOutputTokens
            || (_options.MaxEstimatedCost is { } maxCost
                && entry.EstimatedCost + (request.RequestedEstimatedCost ?? 0) > maxCost);
    }

    private void TrimExpired(long currentWindow)
    {
        foreach (var key in _entries.Keys.Where(key => key.Window < currentWindow))
        {
            _entries.TryRemove(key, out _);
        }
    }

    private static void Validate(NhAiInMemoryBudgetOptions options)
    {
        if (options.MaxCalls < 0
            || options.MaxInputTokens < 0
            || options.MaxOutputTokens < 0
            || options.MaxEstimatedCost < 0
            || options.Window <= TimeSpan.Zero
            || options.MaxActorWindows < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "AI in-memory budget limits must be positive or zero.");
        }
    }

    private static ValueTask<TaskResult<NhAiBudgetReservation>> Failed(string code, string message)
    {
        return ValueTask.FromResult(TaskResult<NhAiBudgetReservation>.Failed(code, message));
    }

    private sealed class Entry
    {
        public object Sync { get; } = new();
        public int Calls { get; set; }
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
        public decimal EstimatedCost { get; set; }
    }
}
