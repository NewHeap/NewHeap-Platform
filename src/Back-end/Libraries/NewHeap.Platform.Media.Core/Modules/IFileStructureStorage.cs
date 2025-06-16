namespace NewHeap.Media.Modules;

public interface IFileStructureStorage
{
    Task<FileReference> CreateFile(FileModel model, Guid id);

    Task<FileReference?> UpdateFile(Guid id, FileModel model);

    Task<FolderReference> CreateFolder(string? path, string folderName);

    Task<bool> DeleteFolder(string? path, string folderName);

    Task<IEnumerable<FileReference>> GetFiles(string? path, string? language);

    Task<FolderContents> GetFolder(string? path, string? language);
    Task<FileReference?> GetFile(string? path, string fileName, string? language);

    Task<bool> DeleteFile(string? path, string filename);

    Task<IEnumerable<FileReference>> Search(string searchTerm, string? path, string? language, string[]? tags);
    Task<bool> Localize(Guid entityId, string language, string propertyName, string value);

    Task<bool> UpdateTags(string path, string fileName, IEnumerable<string> tags);
    Task<FileReference?> GetById(Guid id);
    Task<FolderReference?> MoveFolder(string? path, string folderName, string newPath, string newName);

    Task<FolderReference> GetFolderReference(string? path);
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