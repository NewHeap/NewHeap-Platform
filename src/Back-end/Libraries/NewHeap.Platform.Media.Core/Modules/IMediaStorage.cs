namespace NewHeap.Media.Modules;

public interface IMediaStorage
{
    Task<Guid> SaveFileAsync(Stream file);

    Task<bool> UpdateFileAsync(Stream fileStream, Guid id);

    Task<bool> DeleteAsync(Guid id);
    Task<Stream?> GetFileAsync(Guid fileRefId);
}