using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SampleProjectManagement.Api.Services;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

public sealed class ObservabilitySampleServiceTests
{
    [Fact]
    public async Task RunAsync_EmitsStructuredLogsScopeAndActivityEvidence()
    {
        var logger = new CollectingLogger<ObservabilitySampleService>();
        Activity? stoppedActivity = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "SampleProjectManagement.Api",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => stoppedActivity = activity
        };
        ActivitySource.AddActivityListener(listener);

        var service = new ObservabilitySampleService(logger);
        var result = await service.RunAsync(includeHandledFailure: true, CancellationToken.None);

        Assert.True(result.CompletionLogged);
        Assert.True(result.HandledFailureLogged);
        Assert.False(string.IsNullOrWhiteSpace(result.TraceId));
        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Information
            && entry.Message.Contains("Observability sample completed", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Warning
            && entry.Exception is InvalidOperationException);
        Assert.Contains(logger.Scopes, scope =>
            scope.TryGetValue("sample_case", out var value)
            && Equals(value, "SPM-105"));
        Assert.NotNull(stoppedActivity);
        Assert.Equal("SPM-105", stoppedActivity!.GetTagItem("sample.case"));
        Assert.Equal(ActivityStatusCode.Error, stoppedActivity.Status);
    }

    private sealed class CollectingLogger<T> : ILogger<T>
    {
        public ConcurrentBag<LogEntry> Entries { get; } = [];
        public ConcurrentBag<IReadOnlyDictionary<string, object?>> Scopes { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                Scopes.Add(values.ToDictionary(pair => pair.Key, pair => pair.Value));
            }

            return Scope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));

        public sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

        private sealed class Scope : IDisposable
        {
            public static Scope Instance { get; } = new();
            public void Dispose() { }
        }
    }
}
