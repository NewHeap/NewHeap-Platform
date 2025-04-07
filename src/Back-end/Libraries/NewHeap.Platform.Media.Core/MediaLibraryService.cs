using NewHeap.Media.Modules;
using System.Linq.Expressions;

namespace NewHeap.Media;

public interface IMediaLibraryService
{
    Task RenameFile(string path, string filename, string newPath, string newFilename);
    Task<FileReference> CreateFile(string? path, string filename, Stream file);
    Task<FolderReference> CreateFolder(string? path, string folderName);
    Task<FileReference?> GetFile(string? path, string filename, string? language = null);
    Task<Stream?> DownloadFile(string? path, string fileName);
    Task<FolderContents> GetFolder(string? path, string? language = null);
    Task<bool> UpdateFile(string? path, string fileName, Stream file);
    Task<bool> DeleteFolder(string? path, string folderName);
    Task<bool> DeleteFile(string? path, string fileName);
    Task<IEnumerable<FileReference>> Search(string? path, string searchTerm, string? language = null);
    Task<bool> LocalizeField(Guid fileReferenceId, string propertyName, string language, string value);
}

public class MediaLibraryService : IMediaLibraryService
{
    private readonly IFileStructureStorage _fileStructureStorage;
    private readonly IMediaStorage _fileStorage;
    private readonly IAuthorizationModule _authorizationModule;

    public MediaLibraryService(
        IFileStructureStorage fileStructureStorage,
        IMediaStorage fileStorage,
        IAuthorizationModule authorizationModule
    )
    {
        _fileStructureStorage = fileStructureStorage;
        _fileStorage = fileStorage;
        _authorizationModule = authorizationModule;
    }

    public Task<bool> LocalizeField(Guid fileReferenceId, string propertyName, string language, string value)
    {
        return _fileStructureStorage.Localize(fileReferenceId, language, propertyName, value);
    }


    public async Task RenameFile(string path, string filename, string newPath, string newFilename)
    {
        await EnsureAuthorized(path, filename, null, ActionType.Update);

        var fileRef = await _fileStructureStorage.GetFile(path, filename, null);
        if (fileRef == null)
        {
            return;
        }

        await _fileStructureStorage.DeleteFile(path, filename);
        await _fileStructureStorage.CreateFile(newPath, newFilename, fileRef.Id);
    }

    public async Task<FileReference> CreateFile(string? path, string filename, Stream file)
    {
        await EnsureAuthorized(path, filename, null, ActionType.Create);
        var fileId = await _fileStorage.SaveFile(file);
        var fileRef = await _fileStructureStorage.CreateFile(path, filename, fileId);
        return fileRef;
    }

    public async Task<FolderReference> CreateFolder(string? path, string folderName)
    {
        await EnsureAuthorized(path, null, null, ActionType.Create);
        var folderRef = await _fileStructureStorage.CreateFolder(path, folderName);
        return folderRef;
    }

    public async Task<FileReference?> GetFile(string? path, string filename, string? language = null)
    {
        await EnsureAuthorized(path, filename, null, ActionType.Read);
        var fileRef = await _fileStructureStorage.GetFile(path, filename, language);
        return fileRef;
    }

    public async Task<Stream?> DownloadFile(string? path, string fileName)
    {
        await EnsureAuthorized(path, fileName, null, ActionType.Read);
        var fileRef = await _fileStructureStorage.GetFile(path, fileName, null);
        if (fileRef == null)
        {
            return null;
        }

        return await _fileStorage.GetFile(fileRef.Id);
    }

    public async Task<FolderContents> GetFolder(string? path, string? language)
    {
        await EnsureAuthorized(path, null, language, ActionType.Read);
        var folder = await _fileStructureStorage.GetFolder(path, language);
        return folder;
    }

    public async Task<bool> UpdateFile(string? path, string fileName, Stream file)
    {
        await EnsureAuthorized(path, fileName, null, ActionType.Update);
        var fileRef = await _fileStructureStorage.GetFile(path, fileName, null);
        if (fileRef == null)
        {
            return false;
        }

        return await _fileStorage.UpdateFile(file, fileRef.Id);
    }

    public async Task<bool> DeleteFolder(string? path, string folderName)
    {
        await EnsureAuthorized(path, null, null, ActionType.Delete);

        var files = await _fileStructureStorage.GetFiles(path + "/" + folderName, null);

        var deleted =  await _fileStructureStorage.DeleteFolder(path, folderName);
        if (deleted)
        {
            var ids = files.Select(x => x.Id).ToList();
            foreach (var id in ids)
            {
                await _fileStorage.Delete(id);
            }    
        }
        
        return deleted;
    }

    public async Task<bool> DeleteFile(string? path, string fileName)
    {
        await EnsureAuthorized(path, fileName, null, ActionType.Delete);
        var fileRef = await _fileStructureStorage.GetFile(path, fileName, null);
        if (fileRef == null)
        {
            return false;
        }

        await _fileStorage.Delete(fileRef.Id);
        await _fileStructureStorage.DeleteFile(path, fileName);
        return true;
    }

    public async Task<IEnumerable<FileReference>> Search(string? path, string searchTerm, string? language = null)
    {
        await EnsureAuthorized(path, null, language, ActionType.Read);
        return await _fileStructureStorage.Search(searchTerm, path, language);
    }

    private async Task EnsureAuthorized(
        string? path,
        string? filename,
        string? language,
        ActionType action)
    {
        var context = new AuthorizationContext
        {
            Path = path,
            FileName = filename,
            Language = language,
            Action = action
        };
        await _authorizationModule.IsAuthorized(context);
        if (!context.Authorized)
        {
            throw new UnauthorizedAccessException();
        }
    }
}