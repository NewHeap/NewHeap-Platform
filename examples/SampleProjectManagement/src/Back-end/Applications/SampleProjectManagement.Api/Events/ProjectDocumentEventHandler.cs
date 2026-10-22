using NewHeap.Media.EventHandlers;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using SampleProjectManagement.Api.Composition;
using SampleProjectManagement.Api.Services;

namespace SampleProjectManagement.Api.Events;

/// <summary>
/// SPM-306: a document added to the documents folder of a project in the media library starts the
/// document reader on that project, through its trigger. The event carries ids only; the agent reads
/// the documents through its tool and the assistant's file source.
/// </summary>
public sealed class ProjectDocumentEventHandler(
    INhAssistantAgentTriggers triggers,
    ILogger<ProjectDocumentEventHandler> logger) : IHandleMediaLibraryEvent
{
    public async ValueTask HandleEvent(MediaLibraryFileEvent @event)
    {
        if (@event.Type != MediaLibraryFileEventType.Added
            || @event.NewFile is not { } file
            || !ProjectMediaFolders.TryGetProjectDocuments(file.Folder.FullPath, out var divisionId, out var projectId))
        {
            return;
        }

        try
        {
            await triggers.PublishAsync(new ProjectDocumentAdded(divisionId, projectId, file.Id));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The media library stored the document already; it must not report the upload as failed
            // because the agent could not start.
            logger.LogWarning(
                "The document reader did not start for document {DocumentId}: {ExceptionType}.",
                file.Id,
                exception.GetType().Name);
        }
    }

    public ValueTask HandleEvent(MediaLibraryFolderEvent @event)
    {
        return ValueTask.CompletedTask;
    }
}
