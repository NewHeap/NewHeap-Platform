using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;

namespace NewHeap.Platform.AI.Test;

public sealed class NhAiEvaluationCase
{
    public NhAiEvaluationCase(
        string id,
        IReadOnlyList<ChatMessage> messages,
        ChatResponse modelResponse,
        string expectedOutcomeCode,
        NhAiDataClassification classification,
        NhAiContextTrust trust,
        IReadOnlyList<string> executionScopeKeys,
        IReadOnlyList<string> provenanceReferences,
        IReadOnlyList<EvaluationContext>? contexts = null)
    {
        NhAiEvaluationNames.ValidateDashCase(id, nameof(id));
        NhAiEvaluationNames.ValidateDashCase(expectedOutcomeCode, nameof(expectedOutcomeCode));
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(modelResponse);
        ArgumentNullException.ThrowIfNull(executionScopeKeys);
        ArgumentNullException.ThrowIfNull(provenanceReferences);
        if (messages.Count is < 1 or > 64
            || executionScopeKeys.Count is < 1 or > 64
            || provenanceReferences.Count is < 1 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(messages));
        }
        Id = id;
        Messages = messages;
        ModelResponse = modelResponse;
        ExpectedOutcomeCode = expectedOutcomeCode;
        Classification = classification;
        Trust = trust;
        ExecutionScopeKeys = executionScopeKeys;
        ProvenanceReferences = provenanceReferences;
        Contexts = contexts ?? [];
        ContentHash = NhAiCanonicalJson.ComputeHash(new
        {
            Id,
            Messages = messages.Select(message => new
            {
                Role = message.Role.ToString(),
                message.Text
            }).ToArray(),
            Response = modelResponse.Text,
            ExpectedOutcomeCode,
            Classification,
            Trust,
            ExecutionScopeKeys,
            ProvenanceReferences
        });
    }

    public string Id { get; }
    public IReadOnlyList<ChatMessage> Messages { get; }
    public ChatResponse ModelResponse { get; }
    public string ExpectedOutcomeCode { get; }
    public NhAiDataClassification Classification { get; }
    public NhAiContextTrust Trust { get; }
    public IReadOnlyList<string> ExecutionScopeKeys { get; }
    public IReadOnlyList<string> ProvenanceReferences { get; }
    public IReadOnlyList<EvaluationContext> Contexts { get; }
    public string ContentHash { get; }

}

public sealed class NhAiEvaluationDataset
{
    public NhAiEvaluationDataset(
        string id,
        int version,
        string baselineId,
        int baselineVersion,
        IReadOnlyList<NhAiEvaluationCase> cases)
    {
        NhAiEvaluationNames.ValidateDashCase(id, nameof(id));
        NhAiEvaluationNames.ValidateDashCase(baselineId, nameof(baselineId));
        ArgumentNullException.ThrowIfNull(cases);
        if (version < 1 || baselineVersion < 1 || cases.Count is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(version));
        }
        if (cases.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != cases.Count)
        {
            throw new InvalidOperationException("AI evaluation case identifiers must be unique.");
        }
        Id = id;
        Version = version;
        BaselineId = baselineId;
        BaselineVersion = baselineVersion;
        Cases = cases;
        DatasetHash = NhAiCanonicalJson.ComputeHash(cases
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .Select(item => new { item.Id, item.ContentHash })
            .ToArray());
    }

    public string Id { get; }
    public int Version { get; }
    public string BaselineId { get; }
    public int BaselineVersion { get; }
    public IReadOnlyList<NhAiEvaluationCase> Cases { get; }
    public string DatasetHash { get; }
}

internal static class NhAiEvaluationNames
{
    internal static void ValidateDashCase(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > 128
            || value[0] == '-'
            || value[^1] == '-'
            || value.Contains("--", StringComparison.Ordinal)
            || value.Any(character => (character < 'a' || character > 'z')
                && (character < '0' || character > '9')
                && character != '-'))
        {
            throw new ArgumentException(
                "Evaluation identifiers must use bounded dash-case.",
                parameterName);
        }
    }
}

public sealed record NhAiEvaluationMetricSummary(
    string Name,
    string MetricType,
    string Rating,
    bool Failed,
    IReadOnlyDictionary<string, int> DiagnosticCounts);

public sealed record NhAiEvaluationCaseResult(
    string CaseId,
    string CaseContentHash,
    bool Passed,
    TimeSpan Duration,
    IReadOnlyList<NhAiEvaluationMetricSummary> Metrics);

public sealed record NhAiEvaluationReport(
    string DatasetId,
    int DatasetVersion,
    string DatasetHash,
    string BaselineId,
    int BaselineVersion,
    IReadOnlyList<string> MetricNames,
    IReadOnlyList<NhAiEvaluationCaseResult> Cases,
    bool Passed,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt)
{
    public NhAiRetentionCategory RetentionCategory { get; init; } =
        NhAiRetentionCategory.EvaluationArtifact;
}

public interface INhAiEvaluationReportSink
{
    ValueTask WriteAsync(
        NhAiEvaluationReport report,
        CancellationToken cancellationToken = default);
}

public sealed class NhAiEvaluationRunner(
    IEnumerable<INhAiEvaluationReportSink>? reportSinks = null)
{
    private readonly IReadOnlyList<INhAiEvaluationReportSink> _reportSinks =
        reportSinks?.ToArray() ?? [];

    public async Task<NhAiEvaluationReport> RunAsync(
        NhAiEvaluationDataset dataset,
        IEvaluator evaluator,
        ChatConfiguration? chatConfiguration = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(evaluator);
        var startedAt = DateTimeOffset.UtcNow;
        var results = new List<NhAiEvaluationCaseResult>(dataset.Cases.Count);
        foreach (var evaluationCase in dataset.Cases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var caseStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            var result = await evaluator.EvaluateAsync(
                evaluationCase.Messages,
                evaluationCase.ModelResponse,
                chatConfiguration,
                evaluationCase.Contexts,
                cancellationToken);
            var metrics = result.Metrics.Values
                .OrderBy(metric => metric.Name, StringComparer.Ordinal)
                .Select(CreateSummary)
                .ToArray();
            var passed = metrics.Length > 0 && metrics.All(metric => !metric.Failed);
            results.Add(new NhAiEvaluationCaseResult(
                evaluationCase.Id,
                evaluationCase.ContentHash,
                passed,
                System.Diagnostics.Stopwatch.GetElapsedTime(caseStartedAt),
                metrics));
        }
        var report = new NhAiEvaluationReport(
            dataset.Id,
            dataset.Version,
            dataset.DatasetHash,
            dataset.BaselineId,
            dataset.BaselineVersion,
            evaluator.EvaluationMetricNames.Order(StringComparer.Ordinal).ToArray(),
            results,
            results.All(result => result.Passed),
            startedAt,
            DateTimeOffset.UtcNow);
        foreach (var sink in _reportSinks)
        {
            await sink.WriteAsync(report, cancellationToken);
        }
        return report;
    }

    private static NhAiEvaluationMetricSummary CreateSummary(EvaluationMetric metric)
    {
        var interpretation = metric.Interpretation;
        var rating = interpretation?.Rating.ToString() ?? EvaluationRating.Unknown.ToString();
        var failed = interpretation is null
            || interpretation.Failed
            || interpretation.Rating is EvaluationRating.Unknown or EvaluationRating.Inconclusive;
        var diagnostics = (metric.Diagnostics ?? [])
            .GroupBy(diagnostic => diagnostic.Severity.ToString(), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        return new NhAiEvaluationMetricSummary(
            metric.Name,
            metric.GetType().Name,
            rating,
            failed,
            diagnostics);
    }
}

public sealed class NhAiCapturedEvaluationReportSink : INhAiEvaluationReportSink
{
    private readonly object _sync = new();
    private readonly List<NhAiEvaluationReport> _reports = [];

    public IReadOnlyList<NhAiEvaluationReport> Reports
    {
        get
        {
            lock (_sync)
            {
                return _reports.ToArray();
            }
        }
    }

    public ValueTask WriteAsync(
        NhAiEvaluationReport report,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            _reports.Add(report);
        }
        return ValueTask.CompletedTask;
    }
}
