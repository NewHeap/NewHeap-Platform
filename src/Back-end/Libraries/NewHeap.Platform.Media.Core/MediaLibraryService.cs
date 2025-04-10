using NewHeap.Media.EventHandlers;
using NewHeap.Media.Modules;
using System.Linq.Expressions;
using System.Runtime.InteropServices;

namespace NewHeap.Media;

public interface IMediaLibraryService
{
    Task RenameFile(string path, string filename, string newPath, string newFilename);
    Task<FileReference> CreateFile(FileModel model, Stream file);
    Task<FolderReference> CreateFolder(string? path, string folderName);
    Task<FolderReference?> UpdateFolder(string? path, string folderName, string? newPath, string newName);
    Task<FileReference?> GetFile(string? path, string filename, string? language = null);
    Task<FileReference?> GetFile(Guid id);
    Task<Stream?> DownloadFile(string? path, string fileName);
    Task<FolderContents> GetFolder(string? path, string? language = null);
    Task<bool> UpdateFile(string? path, string fileName, Stream file);
    Task<bool> UpdateFile(Guid id, FileModel model);
    Task<bool> DeleteFolder(string? path, string folderName);
    Task<bool> DeleteFile(string? path, string fileName);

    Task<IEnumerable<FileReference>> Search(string? path, string searchTerm, string? language = null,
        string[]? tags = null);

    Task<bool> LocalizeField(Guid fileReferenceId, string propertyName, string language, string value);

    Task<bool> UpdateFileTags(string? path, string fileName, IEnumerable<string> tags);
}

public class MediaLibraryService : IMediaLibraryService
{
    private readonly IEnumerable<IHandleMediaLibraryEvent> _eventHandlers;
    private readonly IFileStructureStorage _fileStructureStorage;
    private readonly IMediaStorage _fileStorage;
    private readonly IAuthorizationModule _authorizationModule;

    public MediaLibraryService(
        [Optional] IEnumerable<IHandleMediaLibraryEvent> eventHandlers,
        IFileStructureStorage fileStructureStorage,
        IMediaStorage fileStorage,
        IAuthorizationModule authorizationModule
    )
    {
        _eventHandlers = eventHandlers;
        _fileStructureStorage = fileStructureStorage;
        _fileStorage = fileStorage;
        _authorizationModule = authorizationModule;
    }

    public Task<bool> LocalizeField(Guid fileReferenceId, string propertyName, string language, string value)
    {
        return _fileStructureStorage.Localize(fileReferenceId, language, propertyName, value);
    }

    public async Task<bool> UpdateFileTags(string? path, string fileName, IEnumerable<string> tags)
    {
        var current = await _fileStructureStorage.GetFile(path, fileName, null);
        var fileEvent = new MediaLibraryFileEvent
        {
            OldFile = current,
            Type = MediaLibraryFileEventType.Updated
        };
        path ??= "";
        var result = await _fileStructureStorage.UpdateTags(path, fileName, tags);
        
        fileEvent.NewFile = await _fileStructureStorage.GetFile(path, fileName, null);
        await ProcessEvent(fileEvent);
        return result;
    }


    public async Task RenameFile(string path, string filename, string newPath, string newFilename)
    {
        await EnsureAuthorized(path, filename, null, ActionType.Update);

        var fileRef = await _fileStructureStorage.GetFile(path, filename, null);
        if (fileRef == null)
        {
            return;
        }
        var fileEvent = new MediaLibraryFileEvent
        {
            OldFile = fileRef,
            Type = MediaLibraryFileEventType.Updated
        };

        fileRef = await _fileStructureStorage.UpdateFile(fileRef.Id, new FileModel()
        {
            Tags = fileRef.Tags.ToArray(),
            Name = newFilename,
            Path = newPath,
            MetaData = fileRef.MetaData,
            Description = fileRef.Description,
            AltText = fileRef.AltText,
            Title = fileRef.Title,
            Creator = fileRef.Creator,
        });
        fileEvent.NewFile = fileRef;
        await ProcessEvent(fileEvent);
    }

    public async Task<FileReference> CreateFile(FileModel model, Stream file)
    {
        await EnsureAuthorized(model.Path, model.Name, null, ActionType.Create);

        var fileEvent = new MediaLibraryFileEvent
        {
            Type = MediaLibraryFileEventType.Added
        };
        
        var fileId = await _fileStorage.SaveFile(file);

        var fileRef = await _fileStructureStorage.CreateFile(model, fileId);
        fileEvent.NewFile = fileRef;
        await ProcessEvent(fileEvent);
        return fileRef;
    }

    public async Task<FolderReference> CreateFolder(string? path, string folderName)
    {
        await EnsureAuthorized(path, null, null, ActionType.Create);
        var folderRef = await _fileStructureStorage.CreateFolder(path, folderName);
        return folderRef;
    }

    public async Task<FolderReference?> UpdateFolder(string? path, string folderName, string newPath, string newName)
    {
        return await _fileStructureStorage.MoveFolder(path, folderName, newPath, newName);
    }

    public async Task<FileReference?> GetFile(string? path, string filename, string? language = null)
    {
        await EnsureAuthorized(path, filename, null, ActionType.Read);
        var fileRef = await _fileStructureStorage.GetFile(path, filename, language);
        return fileRef;
    }

    public async Task<FileReference?> GetFile(Guid id)
    {
        var reference = await _fileStructureStorage.GetById(id);
        return reference;
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

        var fileEvent = new MediaLibraryFileEvent
        {
            OldFile = fileRef,
            NewFile = fileRef,
            Type = MediaLibraryFileEventType.BinaryUpdated
        };
        var result = await _fileStorage.UpdateFile(file, fileRef.Id);
        await ProcessEvent(fileEvent);

        return result;
    }

    public async Task<bool> UpdateFile(Guid id, FileModel model)
    {
        
        var reference = await _fileStructureStorage.GetById(id);

        if (reference == null)
        {
            return false;
        }
        await EnsureAuthorized(reference.Folder.Path + "/" + reference.Folder.Name, reference.Name, null,
            ActionType.Update);

        var fileEvent = new MediaLibraryFileEvent
        {
            OldFile = reference,
            Type = MediaLibraryFileEventType.Updated
        };
        
        var success = await _fileStructureStorage.UpdateFile(reference.Id, model) != null;

        fileEvent.NewFile = await _fileStructureStorage.GetById(id);
        await ProcessEvent(fileEvent);
        
        return success;
    }

    public async Task<bool> DeleteFolder(string? path, string folderName)
    {
        await EnsureAuthorized(path, null, null, ActionType.Delete);

        var files = await _fileStructureStorage.GetFiles(path + "/" + folderName, null);

        var events = files.Select(x => new MediaLibraryFileEvent
        {
            OldFile = x,
            Type = MediaLibraryFileEventType.Removed
        });
        
        var deleted = await _fileStructureStorage.DeleteFolder(path, folderName);
        if (deleted)
        {
            var ids = files.Select(x => x.Id).ToList();
            foreach (var id in ids)
            {
                await _fileStorage.Delete(id);
            }
        }

        foreach (var @event in events)
        {
            await ProcessEvent(@event);
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

        var @event = new MediaLibraryFileEvent { OldFile = fileRef, Type = MediaLibraryFileEventType.Removed };

        await _fileStorage.Delete(fileRef.Id);
        await _fileStructureStorage.DeleteFile(path, fileName);
        await ProcessEvent(@event);
        return true;
    }

    public async Task<IEnumerable<FileReference>> Search(string? path, string searchTerm, string? language = null,
        string[]? tags = null)
    {
        await EnsureAuthorized(path, null, language, ActionType.Read);
        return await _fileStructureStorage.Search(searchTerm, path, language, tags);
    }

    private async Task ProcessEvent(MediaLibraryFileEvent fileEvent)
    {
        foreach (var handler in _eventHandlers)
        {
            await handler.HandleEvent(fileEvent);
        }
    }

    private async Task EnsureAuthorized(
        string? path,
        string? filename,
        string? language,
        ActionType action)
    {
        var context = new AuthorizationContext
        {
            Path = path, FileName = filename, Language = language, Action = action
        };
        await _authorizationModule.IsAuthorized(context);
        if (!context.Authorized)
        {
            throw new UnauthorizedAccessException();
        }
    }
}