namespace NewHeap.Platform.AI.Test;

public sealed class NhAiCapturedAuditSink : INhAiAuditSink
{
    private readonly object _sync = new();
    private readonly List<NhAiAuditRecord> _records = [];

    public IReadOnlyList<NhAiAuditRecord> Records
    {
        get
        {
            lock (_sync)
            {
                return _records.ToArray();
            }
        }
    }

    public ValueTask WriteAsync(
        NhAiAuditRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            _records.Add(record);
        }
        return ValueTask.CompletedTask;
    }
}

public sealed class NhAiCapturedUsageSink : INhAiUsageSink
{
    private readonly object _sync = new();
    private readonly List<NhAiUsageRecord> _records = [];

    public IReadOnlyList<NhAiUsageRecord> Records
    {
        get
        {
            lock (_sync)
            {
                return _records.ToArray();
            }
        }
    }

    public ValueTask WriteAsync(
        NhAiUsageRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            _records.Add(record);
        }
        return ValueTask.CompletedTask;
    }
}
