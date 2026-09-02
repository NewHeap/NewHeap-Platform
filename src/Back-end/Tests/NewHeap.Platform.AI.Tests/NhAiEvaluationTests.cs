using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace NewHeap.Platform.AI.Tests;

public sealed class NhAiEvaluationTests
{
    [Fact]
    public async Task Evaluation_report_is_versioned_content_free_and_fails_closed()
    {
        const string prompt = "protected-evaluation-prompt";
        const string response = "protected-evaluation-response";
        var dataset = new NhAiEvaluationDataset(
            "project-agent-safety",
            1,
            "project-agent-baseline",
            3,
            [
                new NhAiEvaluationCase(
                    "prompt-injection-remains-data",
                    [new ChatMessage(ChatRole.User, prompt)],
                    new ChatResponse(new ChatMessage(ChatRole.Assistant, response)),
                    "refused-untrusted-instruction",
                    NhAiDataClassification.Internal,
                    NhAiContextTrust.UntrustedRetrieved,
                    ["division:division-1"],
                    ["project:project-1", "field:description"])
            ]);
        var sink = new NhAiCapturedEvaluationReportSink();
        var runner = new NhAiEvaluationRunner([sink]);

        var report = await runner.RunAsync(dataset, new InconclusiveEvaluator());

        Assert.False(report.Passed);
        Assert.False(Assert.Single(report.Cases).Passed);
        var metric = Assert.Single(Assert.Single(report.Cases).Metrics);
        Assert.True(metric.Failed);
        Assert.Equal("Inconclusive", metric.Rating);
        Assert.Equal(dataset.DatasetHash, report.DatasetHash);
        Assert.Equal(3, report.BaselineVersion);
        Assert.Same(report, Assert.Single(sink.Reports));
        var serialized = JsonSerializer.Serialize(report);
        Assert.DoesNotContain(prompt, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(response, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("diagnostic-protected-content", serialized, StringComparison.Ordinal);
    }

    private sealed class InconclusiveEvaluator : IEvaluator
    {
        public IReadOnlyCollection<string> EvaluationMetricNames => ["prompt-injection-resistance"];

        public ValueTask<EvaluationResult> EvaluateAsync(
            IEnumerable<ChatMessage> messages,
            ChatResponse modelResponse,
            ChatConfiguration? chatConfiguration,
            IEnumerable<EvaluationContext>? additionalContext,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var metric = new BooleanMetric(
                "prompt-injection-resistance",
                null,
                "diagnostic-protected-content")
            {
                Interpretation = new EvaluationMetricInterpretation(
                    EvaluationRating.Inconclusive,
                    failed: false,
                    reason: "diagnostic-protected-content"),
                Diagnostics =
                [
                    EvaluationDiagnostic.Warning("diagnostic-protected-content")
                ]
            };
            return ValueTask.FromResult(new EvaluationResult(metric));
        }
    }
}
