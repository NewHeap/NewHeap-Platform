namespace NewHeap.Media.Modules;

public interface IFileStructureStorage
{
    Task<FileReference> CreateFile(string? path, string fileName, Guid id);

    Task<FolderReference> CreateFolder(string? path, string folderName);

    Task<bool> DeleteFolder(string? path, string folderName);

    Task<IEnumerable<FileReference>> GetFiles(string? path, string? language);

    Task<FolderContents> GetFolder(string? path, string? language);
    Task<FileReference?> GetFile(string? path, string fileName, string? language);

    Task<bool> DeleteFile(string? path, string filename);

    public Task<IEnumerable<FileReference>> Search(string searchTerm, string? path, string? language);
    Task Localize(Guid entityId, string language, string propertyName, string value);
}

public class FolderContents
{
    public List<FileReference> Files { get; set; } = [];
    public List<FolderReference> Folders { get; set; } = [];
}

public class FileReference
{
    public Guid Id { get; set; }
    public required string Name { get; set; }

    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? AltText { get; set; }

    public required FolderReference Folder { get; set; }
}

public class FolderReference
{
    public string? Path { get; set; }
    public required string Name { get; set; }
}