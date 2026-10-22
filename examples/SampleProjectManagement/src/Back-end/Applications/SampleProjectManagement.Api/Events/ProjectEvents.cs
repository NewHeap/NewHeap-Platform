using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.Common.Events;
using NewHeap.Platform.Events.Cap;
using SampleProjectManagement.Core.Events;
using SampleProjectManagement.Core.Services;
using System.Collections.Concurrent;
using System.Text.Json;

namespace SampleProjectManagement.Api.Events;

[NhMessageProcessing(MessageProcessingType.PerApplication)]
// One application group lets multiple instances compete for the same message;
// choose PerInstance only when every instance must process the event itself.
public sealed class ProjectEventConsumer : INhEventConsumer<ProjectCreatedEvent>
{
    private readonly SampleEventLog _eventLog;
    private readonly INhAssistantAgentTriggers _agentTriggers;

    public ProjectEventConsumer(SampleEventLog eventLog, INhAssistantAgentTriggers agentTriggers)
    {
        _eventLog = eventLog;
        _agentTriggers = agentTriggers;
    }

    public async Task HandleAsync(ProjectCreatedEvent @event, CancellationToken cancellationToken)
    {
        _eventLog.Add(@event);

        // The consumer runs after the project transaction committed, so a rolled-back project
        // never starts the intake reviewer. A redelivered event finds the run its event id
        // already started.
        await _agentTriggers.PublishAsync(@event, cancellationToken);
    }
}


/// <summary>
/// SPM-282: a task update reaches the platform's agent triggers after the task transaction
/// committed. The intake reviewer registers a proposal revalidation for the event, so a task that
/// someone ticked off by hand closes the agent's proposal to complete it, live and with the reason.
/// </summary>
[NhMessageProcessing(MessageProcessingType.PerApplication)]
public sealed class ProjectTaskEventConsumer(INhAssistantAgentTriggers agentTriggers) : INhEventConsumer<ProjectTaskUpdatedEvent>
{
    public async Task HandleAsync(ProjectTaskUpdatedEvent @event, CancellationToken cancellationToken)
    {
        await agentTriggers.PublishAsync(@event, cancellationToken);
    }
}

/// <summary>
/// SPM-282: the explicit form. After a project's status changed, the application revalidates the
/// open agent proposals about the project itself; a proposal to set the status it already has closes
/// as already done.
/// SPM-308: the change then reaches the agent triggers on the status-changed event, whose filter skips
/// an agent's own change on the project.
/// </summary>
[NhMessageProcessing(MessageProcessingType.PerApplication)]
public sealed class ProjectStatusEventConsumer(
    INhAssistantProposalRevalidation revalidation,
    INhAssistantAgentTriggers agentTriggers)
    : INhEventConsumer<ProjectStatusChangedEvent>
{
    public async Task HandleAsync(ProjectStatusChangedEvent @event, CancellationToken cancellationToken)
    {
        await revalidation.RevalidateSubjectAsync(ProjectSubjects.For(@event.ProjectId), cancellationToken);
        await agentTriggers.PublishAsync(@event, cancellationToken);
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
