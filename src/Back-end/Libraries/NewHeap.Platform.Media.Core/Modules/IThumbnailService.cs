using NewHeap.Media.EventHandlers;

namespace NewHeap.Media.Modules;

public interface IThumbnailService
{
    Task<string?> GetThumbnailAsync(Guid id);
}

public class ThumbnailService : IThumbnailService
{
    public Task<string?> GetThumbnailAsync(Guid id)
    {
        return Task.FromResult<string?>(null);
    }
}

public abstract class GeneratedThumbnailServiceBase : IThumbnailService, IHandleMediaLibraryEvent
{
    public abstract Task<string?> GetThumbnailAsync(Guid id);

    public abstract Task UpdateThumbnailAsync(FileReference file);
    
    public virtual async ValueTask HandleEvent(MediaLibraryFileEvent message)
    {
        if (message.Type == MediaLibraryFileEventType.BinaryUpdated || message.Type == MediaLibraryFileEventType.Added)
        {
            await UpdateThumbnailAsync(message.NewFile!);
        }
    }

    public virtual ValueTask HandleEvent(MediaLibraryFolderEvent @event)
    {
        return ValueTask.CompletedTask;
    }
}