using NewHeap.Platform.Common.Events;
using NewHeap.Platform.Events.Cap;
using SampleProjectManagement.Core.Events;
using System.Collections.Concurrent;
using System.Text.Json;

namespace SampleProjectManagement.Api.Events;

[NhMessageProcessing(MessageProcessingType.PerApplication)]
// One application group lets multiple instances compete for the same message;
// choose PerInstance only when every instance must process the event itself.
public sealed class ProjectEventConsumer : INhEventConsumer<ProjectCreatedEvent>
{
    private readonly SampleEventLog _eventLog;

    public ProjectEventConsumer(SampleEventLog eventLog)
    {
        _eventLog = eventLog;
    }

    public Task HandleAsync(ProjectCreatedEvent @event, CancellationToken cancellationToken)
    {
        _eventLog.Add(@event);
        return Task.CompletedTask;
    }
}


public sealed class ProjectPrioritySampleEvent : INhEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();
    public Guid ProjectId { get; init; }
    public int Priority { get; init; }
    public static string Topic => PriorityProjectEventConsumer.Topic;
}

public sealed class PriorityProjectEventConsumer : INhCustomTopicEventConsumer
{
    private readonly ILogger<PriorityProjectEventConsumer> _logger;

    public PriorityProjectEventConsumer(ILogger<PriorityProjectEventConsumer> logger)
    {
        _logger = logger;
    }

    public static string Topic => "sample-project-management.project-priority";

    public Task HandleAsync(JsonElement @event, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Priority project event received: {Event}", @event.GetRawText());
        return Task.CompletedTask;
    }
}

public sealed class SampleEventLog
{
    private readonly ConcurrentQueue<ProjectCreatedEvent> _events = new();
    private readonly ConcurrentDictionary<Guid, byte> _processedEventIds = new();

    public IReadOnlyCollection<ProjectCreatedEvent> Events => _events.ToArray();

    public void Add(ProjectCreatedEvent @event)
    {
        if (!_processedEventIds.TryAdd(@event.EventId, 0))
        {
            return;
        }
        _events.Enqueue(@event);
        while (_events.Count > 20)
        {
            _events.TryDequeue(out _);
        }
    }
}
