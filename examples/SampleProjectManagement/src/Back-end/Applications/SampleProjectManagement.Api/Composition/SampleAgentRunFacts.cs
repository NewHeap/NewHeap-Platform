using System.Collections.Concurrent;
using NewHeap.Platform.AI.Chat;

namespace SampleProjectManagement.Api.Composition;

/// <summary>
/// SPM-304: what the sample's agents did, counted per agent and skill from the content-free
/// <see cref="NhAssistantAuditEventKind.AgentRunEnded"/> events alone, as an application's usage
/// report or billing would: runs, runs without a model call (the agent's rules did the work), shadow
/// runs, failed runs, model calls and what started the runs. It never reads the assistant's tables,
/// and the events carry no content.
/// </summary>
public sealed class SampleAgentRunFacts
{
    private readonly ConcurrentDictionary<(string AgentId, string SkillId), Counter> _counters = new();

    /// <summary>
    /// The counts of the runs of <paramref name="agentId"/> in <paramref name="skillId"/>, or of its
    /// runs outside a skill when <paramref name="skillId"/> is <see langword="null"/>.
    /// </summary>
    public SampleAgentRunCounts Get(string agentId, string? skillId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        return _counters.TryGetValue((agentId, skillId ?? string.Empty), out var counter)
            ? counter.Snapshot()
            : SampleAgentRunCounts.None;
    }

    /// <summary>
    /// Counts an ended run; other events are not run facts.
    /// </summary>
    public void Record(NhAssistantAuditEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (evt.Kind != NhAssistantAuditEventKind.AgentRunEnded)
        {
            return;
        }

        var counter = _counters.GetOrAdd((evt.AgentId, evt.SkillId ?? string.Empty), _ => new Counter());
        counter.Add(evt);
    }

    private sealed class Counter
    {
        private readonly Lock _sync = new();
        private readonly Dictionary<string, int> _triggerKinds = new(StringComparer.Ordinal);
        private int _runs;
        private int _ruleOnlyRuns;
        private int _shadowRuns;
        private int _failedRuns;
        private long _modelCalls;

        public void Add(NhAssistantAuditEvent evt)
        {
            lock (_sync)
            {
                _runs++;
                var modelCalls = evt.ModelCallCount ?? 0;
                _modelCalls += modelCalls;
                if (modelCalls == 0)
                {
                    _ruleOnlyRuns++;
                }
                if (evt.Shadow == true)
                {
                    _shadowRuns++;
                }
                if (evt.RunStatus == NhAssistantRunStatuses.Failed)
                {
                    _failedRuns++;
                }

                var kind = evt.TriggerKind ?? "unknown";
                _triggerKinds[kind] = _triggerKinds.GetValueOrDefault(kind) + 1;
            }
        }

        public SampleAgentRunCounts Snapshot()
        {
            lock (_sync)
            {
                return new SampleAgentRunCounts(
                    _runs,
                    _ruleOnlyRuns,
                    _shadowRuns,
                    _failedRuns,
                    _modelCalls,
                    new Dictionary<string, int>(_triggerKinds, StringComparer.Ordinal));
            }
        }
    }
}

/// <summary>
/// The counts of one agent and skill in <see cref="SampleAgentRunFacts"/>.
/// </summary>
/// <param name="RuleOnlyRuns">Runs that made no model call: the agent's rules did the work.</param>
/// <param name="TriggerKinds">The runs per kind of trigger, one of <see cref="NhAssistantTriggerKinds"/>.</param>
public sealed record SampleAgentRunCounts(
    int Runs,
    int RuleOnlyRuns,
    int ShadowRuns,
    int FailedRuns,
    long ModelCalls,
    IReadOnlyDictionary<string, int> TriggerKinds)
{
    public static SampleAgentRunCounts None { get; } = new(0, 0, 0, 0, 0, new Dictionary<string, int>());

    public int RunsStartedBy(string triggerKind)
    {
        return TriggerKinds.GetValueOrDefault(triggerKind);
    }
}

/// <summary>
/// The business audit sink that feeds <see cref="SampleAgentRunFacts"/>.
/// </summary>
public sealed class SampleAgentRunFactsSink(SampleAgentRunFacts facts) : INhAssistantBusinessAuditSink
{
    public ValueTask RecordAsync(NhAssistantAuditEvent evt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        facts.Record(evt);
        return ValueTask.CompletedTask;
    }
}
