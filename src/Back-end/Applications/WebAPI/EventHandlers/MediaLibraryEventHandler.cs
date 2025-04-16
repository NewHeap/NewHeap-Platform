using NewHeap.Media.EventHandlers;
using System;
using System.Threading.Tasks;

namespace WebAPI.EventHandlers;

public class MediaLibraryEventHandler : IHandleMediaLibraryEvent
{
    public async ValueTask HandleEvent(MediaLibraryFileEvent @event)
    {
        Console.WriteLine($"Processed event for {@event.OldFile?.Name ?? @event.NewFile?.Name} with type {@event.Type}");
    }

    public async ValueTask HandleEvent(MediaLibraryFolderEvent @event)
    {
        Console.WriteLine($"Processed folder event for {@event.OldFolder?.Name ?? @event.NewFolder?.Name} with type {@event.Type}");
    }
}