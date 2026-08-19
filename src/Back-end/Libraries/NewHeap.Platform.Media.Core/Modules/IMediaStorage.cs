using NewHeap.Platform.Common.Models;

namespace NewHeap.Media.Modules;

public interface IMediaStorage
{
    Task<Guid> SaveFileAsync(Stream file);

    Task<TaskResult> UpdateFileAsync(Stream fileStream, Guid id);

    Task<TaskResult> DeleteAsync(Guid id);
    Task<Stream?> GetFileAsync(Guid fileRefId);
}