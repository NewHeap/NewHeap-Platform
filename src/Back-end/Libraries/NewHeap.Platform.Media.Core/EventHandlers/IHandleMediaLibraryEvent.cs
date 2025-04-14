using NewHeap.Media.Modules;

namespace NewHeap.Media.EventHandlers;

public interface IHandleMediaLibraryEvent
{
    ValueTask HandleEvent(MediaLibraryFileEvent @event);
}

public class MediaLibraryFileEvent
{
    public FileReference? OldFile { get; internal set; }
    public FileReference? NewFile { get; internal set; }
    public Guid? Id { get; internal set; }

    public MediaLibraryFileEventType Type { get; internal set; }
    
    internal MediaLibraryFileEvent()
    {
    }
}

public enum MediaLibraryFileEventType
{
    /// <summary>
    /// File was created 
    /// </summary>
    Added,
    /// <summary>
    /// File was deleted
    /// </summary>
    Removed,
    /// <summary>
    /// File metadata was updated, e.g. tags or file path/name
    /// </summary>
    Updated,
    
    /// <summary>
    /// File binary data was updated. Metadata may also have changed.
    /// </summary>
    BinaryUpdated
}