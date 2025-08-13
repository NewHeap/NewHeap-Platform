using NewHeap.Media.Models;

namespace NewHeap.Media.Modules;

public interface IFileStructureStorage
{
    Task<FileReference> CreateFileAsync(FileModel model, Guid id);

    Task<FileReference?> UpdateFileAsync(Guid id, FileModel model);

    Task<FolderReference> CreateFolderAsync(string? path, string folderName);

    Task<bool> DeleteFolderAsync(string? path, string folderName);

    Task<IEnumerable<FileReference>> GetFilesAsync(string? path, string? language);

    Task<FolderContents> GetFolderAsync(string? path, string? language);
    Task<FileReference?> GetFileAsync(string? path, string fileName, string? language);

    Task<bool> DeleteFileAsync(string? path, string filename);

    Task<IEnumerable<FileReference>> SearchAsync(string searchTerm, string? path, SearchOptions options);
    Task<bool> LocalizeAsync(Guid entityId, string language, string propertyName, string value);

    Task<bool> UpdateTagsAsync(string path, string fileName, IEnumerable<string> tags);
    Task<FileReference?> GetByIdAsync(Guid id);
    Task<FolderReference?> MoveFolderAsync(string? path, string folderName, string newPath, string newName);

    Task<FolderReference> GetFolderReferenceAsync(string? path);
}

public class FileModel
{
    public string? Name { get; set; }
    public string[]? Tags { get; set; }
    public string? AltText { get; set; }
    public string? Description { get; set; }
    public string? Creator { get; set; }
    public string? Title { get; set; }
    public string? Path { get; set; }
    public Dictionary<string, object>? MetaData { get; set; }
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
    public string? Creator { get; set; }

    public string? Thumbnail { get; set; }
    
    public Dictionary<string, object>? MetaData { get; set; }
    public IEnumerable<string> Tags { get; set; } = [];

    public required FolderReference Folder { get; set; }

    public FileReference Copy(Action<FileReference>? setValues = null)
    {
        var v = new FileReference()
        {
            Folder = Folder,
            Name = Name,
            Tags = Tags,
            Description = Description,
            AltText = AltText,
            Title = Title,
            MetaData = MetaData,
            Creator = Creator,
            Id = Id,
        };
        setValues?.Invoke(v);
        return v;
    }
}

public class FolderReference
{
    public Guid? Id { get; set; }
    public string? Path { get; set; }
    public required string Name { get; set; }
    public required string FullPath { get; set; }

    public FolderReference()
    {
        
    }

    public FolderReference? Copy(Action<FolderReference>? setValues = null)
    {
        var v = new FolderReference
        {
            Id = Id,
            Path = Path,
            Name = Name,
            FullPath = FullPath,
        };
        setValues?.Invoke(v);
        
        return v;
    }
}