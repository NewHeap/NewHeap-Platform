namespace NewHeap.Media.Modules;

public interface IMediaStorage
{
    Task<Guid> SaveFile(Stream file);

    Task<bool> UpdateFile(Stream fileStream, Guid id);

    Task<bool> Delete(Guid id);
    Task<Stream?> GetFile(Guid fileRefId);
}