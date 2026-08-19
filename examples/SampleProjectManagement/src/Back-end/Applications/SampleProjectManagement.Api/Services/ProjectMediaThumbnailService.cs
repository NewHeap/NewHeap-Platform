using System.Collections.Concurrent;
using NewHeap.Media.EventHandlers;
using NewHeap.Media.Modules;

namespace SampleProjectManagement.Api.Services;

public sealed class ProjectMediaThumbnailService : GeneratedThumbnailServiceBase
{
    private readonly SampleMediaThumbnailStore _store;

    public ProjectMediaThumbnailService(SampleMediaThumbnailStore store)
    {
        _store = store;
    }

    public override Task<string?> GetThumbnailAsync(Guid id) =>
        Task.FromResult(_store.Get(id));

    public override Task UpdateThumbnailAsync(FileReference file)
    {
        var extension = Path.GetExtension(file.Name).TrimStart('.').ToUpperInvariant();
        var label = string.IsNullOrWhiteSpace(extension) ? "FILE" : extension[..Math.Min(extension.Length, 8)];
        var svg = $"<svg xmlns='http://www.w3.org/2000/svg' width='160' height='96' viewBox='0 0 160 96'><rect width='160' height='96' rx='12' fill='#263b33'/><text x='80' y='55' text-anchor='middle' font-family='sans-serif' font-size='22' fill='#d8ff69'>{label}</text></svg>";
        _store.Set(file.Id, $"data:image/svg+xml,{Uri.EscapeDataString(svg)}");
        return Task.CompletedTask;
    }

    public override async ValueTask HandleEvent(MediaLibraryFileEvent message)
    {
        if (message.Type == MediaLibraryFileEventType.Removed && message.Id.HasValue)
        {
            _store.Remove(message.Id.Value);
            return;
        }

        await base.HandleEvent(message);
    }
}

public sealed class SampleMediaThumbnailStore
{
    private readonly ConcurrentDictionary<Guid, string> _items = new();

    public int Count => _items.Count;
    public IReadOnlyCollection<Guid> FileIds => _items.Keys.Order().ToArray();

    public string? Get(Guid id) => _items.TryGetValue(id, out var value) ? value : null;
    public void Set(Guid id, string value) => _items[id] = value;
    public void Remove(Guid id) => _items.TryRemove(id, out _);
}
