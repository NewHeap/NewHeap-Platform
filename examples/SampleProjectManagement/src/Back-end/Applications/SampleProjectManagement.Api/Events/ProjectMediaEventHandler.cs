using System.Collections.Concurrent;
using NewHeap.Media.EventHandlers;

namespace SampleProjectManagement.Api.Events;

public sealed class ProjectMediaEventHandler : IHandleMediaLibraryEvent
{
    private readonly SampleMediaEventLog _eventLog;
    private readonly ILogger<ProjectMediaEventHandler> _logger;

    public ProjectMediaEventHandler(
        SampleMediaEventLog eventLog,
        ILogger<ProjectMediaEventHandler> logger)
    {
        _eventLog = eventLog;
        _logger = logger;
    }

    public ValueTask HandleEvent(MediaLibraryFileEvent @event)
    {
        var name = @event.NewFile?.Name ?? @event.OldFile?.Name ?? string.Empty;
        _eventLog.Record(new SampleMediaEvent(
            DateTimeOffset.UtcNow,
            "file",
            @event.Type.ToString(),
            @event.Id,
            name));
        _logger.LogInformation(
            "Media file event {EventType} for {FileId} {FileName}",
            @event.Type,
            @event.Id,
            name);
        return ValueTask.CompletedTask;
    }

    public ValueTask HandleEvent(MediaLibraryFolderEvent @event)
    {
        var name = @event.NewFolder?.FullPath ?? @event.OldFolder?.FullPath ?? string.Empty;
        _eventLog.Record(new SampleMediaEvent(
            DateTimeOffset.UtcNow,
            "folder",
            @event.Type.ToString(),
            @event.Id,
            name));
        _logger.LogInformation(
            "Media folder event {EventType} for {FolderId} {FolderPath}",
            @event.Type,
            @event.Id,
            name);
        return ValueTask.CompletedTask;
    }
}

public sealed record SampleMediaEvent(
    DateTimeOffset OccurredAtUtc,
    string ResourceType,
    string EventType,
    Guid? ResourceId,
    string Name);

public sealed class SampleMediaEventLog
{
    private readonly ConcurrentQueue<SampleMediaEvent> _events = new();

    public IReadOnlyList<SampleMediaEvent> Events =>
        _events.Reverse().Take(100).ToArray();

    public void Record(SampleMediaEvent @event)
    {
        _events.Enqueue(@event);
        while (_events.Count > 200)
        {
            _events.TryDequeue(out _);
        }
    }
}
