using NewHeap.Media.EventHandlers;
using NewHeap.Media.Models;
using NewHeap.Media.Modules;
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
    Task<Stream?> DownloadFile(Guid id);
    Task<FolderContents> GetFolder(string? path, string? language = null);
    Task<bool> UpdateFile(string? path, string fileName, Stream file);
    Task<bool> UpdateFile(Guid id, FileModel model);
    Task<bool> DeleteFolder(string? path, string folderName);
    Task<bool> DeleteFile(string? path, string fileName);

    Task<IEnumerable<FileReference>> Search(string? path, string searchTerm, SearchOptions options);

    Task<bool> LocalizeField(Guid fileReferenceId, string propertyName, string language, string value);

    Task<bool> UpdateFileTags(string? path, string fileName, IEnumerable<string> tags);
}

public class MediaLibraryService : IMediaLibraryService
{
    private readonly IEnumerable<IHandleMediaLibraryEvent> _eventHandlers;
    private readonly IThumbnailService _thumbnailService;
    private readonly IFileStructureStorage _fileStructureStorage;
    private readonly IMediaStorage _fileStorage;
    private readonly IAuthorizationModule _authorizationModule;

    public MediaLibraryService(
        [Optional] IEnumerable<IHandleMediaLibraryEvent> eventHandlers,
        IThumbnailService thumbnailService,
        IFileStructureStorage fileStructureStorage,
        IMediaStorage fileStorage,
        IAuthorizationModule authorizationModule
    )
    {
        _eventHandlers = eventHandlers;
        _thumbnailService = thumbnailService;
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

        if (current == null)
        {
            return false;
        }

        path ??= "";
        var newRef = current.Copy(x => { x.Tags = tags; });

        await TriggerEvents(current, newRef, MediaLibraryFileEventType.Updating);
        var result = await _fileStructureStorage.UpdateTags(path, fileName, tags);
        await TriggerEvents(current, await _fileStructureStorage.GetFile(path, fileName, null),
            MediaLibraryFileEventType.Updated);
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

        var newRef = fileRef.Copy(x =>
        {
            MediaLibraryPath.Split(path, out var folderPath, out var folderName);
            x.Name = newFilename;
            x.Folder = new FolderReference { Name = folderName, Path = folderPath, FullPath = path };
        });
        await TriggerEvents(fileRef, newRef, MediaLibraryFileEventType.Updating);

        newRef = await _fileStructureStorage.UpdateFile(fileRef.Id, new FileModel()
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
        await TriggerEvents(fileRef, newRef, MediaLibraryFileEventType.Updating);
    }

    public async Task<FileReference> CreateFile(FileModel model, Stream file)
    {
        await EnsureAuthorized(model.Path, model.Name, null, ActionType.Create);

        MediaLibraryPath.Split(model.Path ?? NhMediaValues.DirectorySeparator, out var folderPath, out var folderName);
        var newRef = new FileReference()
        {
            Name = model.Name!,
            Folder = new FolderReference() { Path = folderPath, Name = folderName, FullPath = model.Path ?? "/" },
        };

        await TriggerEvents(null, newRef, MediaLibraryFileEventType.Adding);

        var fileId = await _fileStorage.SaveFile(file);
        var fileRef = await _fileStructureStorage.CreateFile(model, fileId);

        await TriggerEvents(null, fileRef, MediaLibraryFileEventType.Added);
        return fileRef;
    }

    public async Task<FolderReference> CreateFolder(string? path, string folderName)
    {
        await EnsureAuthorized(path, null, null, ActionType.Create);

        var newRef = new FolderReference()
        {
            Name = folderName,
            Path = path ?? NhMediaValues.DirectorySeparator,
            FullPath = Path.Combine(path ?? NhMediaValues.DirectorySeparator, folderName),
        };
        await TriggerEvents(null, newRef, MediaLibraryFolderEventType.Adding);
        var folderRef = await _fileStructureStorage.CreateFolder(path, folderName);
        await TriggerEvents(null, folderRef, MediaLibraryFolderEventType.Added);
        return folderRef;
    }

    public async Task<FolderReference?> UpdateFolder(string? path, string folderName, string? newPath, string newName)
    {
        await EnsureAuthorized(MediaLibraryPath.Combine(path, folderName), null, null, ActionType.Update);

        var reference = await _fileStructureStorage.GetFolderReference(MediaLibraryPath.Combine(path, folderName));
        var newRef = reference.Copy(x =>
        {
            x.Path = newPath;
            x.Name = newName;
        });

        await TriggerEvents(reference, newRef, MediaLibraryFolderEventType.Updated);
        newRef = await _fileStructureStorage.MoveFolder(path, folderName, newPath ?? NhMediaValues.DirectorySeparator,
            newName);
        await TriggerEvents(null, newRef, MediaLibraryFolderEventType.Updated);
        return newRef;
    }

    public async Task<FileReference?> GetFile(string? path, string filename, string? language = null)
    {
        await EnsureAuthorized(path, filename, null, ActionType.Read);
        var fileRef = await _fileStructureStorage.GetFile(path, filename, language);
        if (fileRef != null)
        {
            fileRef.Thumbnail = await _thumbnailService.GetThumbnail(fileRef.Id);
        }
        return fileRef;
    }

    public async Task<FileReference?> GetFile(Guid id)
    {
        var reference = await _fileStructureStorage.GetById(id);
        if (reference != null)
        {
            reference.Thumbnail = await _thumbnailService.GetThumbnail(reference.Id);
        }
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

    public async Task<Stream?> DownloadFile(Guid id)
    {
        var fileRef = await _fileStructureStorage.GetById(id);
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
        foreach (var file in folder.Files)
        {
            file.Thumbnail = await _thumbnailService.GetThumbnail(file.Id);
        }
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

        await TriggerEvents(fileRef, fileRef, MediaLibraryFileEventType.Updating);
        var result = await _fileStorage.UpdateFile(file, fileRef.Id);
        await TriggerEvents(fileRef, fileRef, MediaLibraryFileEventType.BinaryUpdated);
        return result;
    }

    public async Task<bool> UpdateFile(Guid id, FileModel model)
    {
        var reference = await _fileStructureStorage.GetById(id);

        if (reference == null)
        {
            return false;
        }

        await EnsureAuthorized(MediaLibraryPath.Combine(reference.Folder.Path, reference.Folder.Name), reference.Name,
            null,
            ActionType.Update);

        var folder = await _fileStructureStorage.GetFolderReference(model.Path);
        var newRef = reference.Copy(x =>
        {
            x.Tags = model.Tags?.ToList() ?? [];
            x.Name = model.Name!;
            x.Folder = folder;
            x.MetaData = model.MetaData;
            x.Description = model.Description;
            x.AltText = model.AltText;
        });

        await TriggerEvents(reference, newRef, MediaLibraryFileEventType.Updating);
        var success = await _fileStructureStorage.UpdateFile(reference.Id, model) != null;
        await TriggerEvents(reference, await _fileStructureStorage.GetById(id), MediaLibraryFileEventType.Updated);
        return success;
    }

    public async Task<bool> DeleteFolder(string? path, string folderName)
    {
        var folderPath = MediaLibraryPath.Combine(path, folderName);
        await EnsureAuthorized(folderPath, null, null, ActionType.Delete);

        var files = (await _fileStructureStorage.GetFiles(folderPath, null)).ToList();

        var folder = await _fileStructureStorage.GetFolderReference(folderPath);

        await TriggerEvents(folder, null, MediaLibraryFolderEventType.Removing);

        var deleted = await _fileStructureStorage.DeleteFolder(path, folderName);
        if (!deleted)
        {
            return false;
        }

        await TriggerEvents(folder, null, MediaLibraryFolderEventType.Removed);
        
        foreach (var file in files)
        {
            await TriggerEvents(file, null, MediaLibraryFileEventType.Removing);
        }

        var ids = files.Select(x => x.Id).ToList();
        foreach (var id in ids)
        {
            await _fileStorage.Delete(id);
        }

        foreach (var file in files)
        {
            await TriggerEvents(file, null, MediaLibraryFileEventType.Removed);
        }

        return true;
    }

    public async Task<bool> DeleteFile(string? path, string fileName)
    {
        await EnsureAuthorized(path, fileName, null, ActionType.Delete);
        var fileRef = await _fileStructureStorage.GetFile(path, fileName, null);
        if (fileRef == null)
        {
            return false;
        }

        await TriggerEvents(fileRef, null, MediaLibraryFileEventType.Removing);
        await _fileStorage.Delete(fileRef.Id);
        await _fileStructureStorage.DeleteFile(path, fileName);
        await TriggerEvents(fileRef, null, MediaLibraryFileEventType.Removed);
        return true;
    }

    public async Task<IEnumerable<FileReference>> Search(string? path, string searchTerm, SearchOptions options)
    {
        await EnsureAuthorized(path, null, options.Language, ActionType.Read);

        NormalizeOptions(options);
        
        var results = (await _fileStructureStorage.Search(searchTerm, path, options)).ToList();
        foreach (var file in results)
        {
            file.Thumbnail = await _thumbnailService.GetThumbnail(file.Id);
        }
        
        return results;
    }

    private void NormalizeOptions(SearchOptions options)
    {
        if (options.IncludedExtensions != null)
        {
            options.IncludedExtensions = options.IncludedExtensions.Select(x => x.Trim().ToLower())
                .Select(x => x.StartsWith('.') ? x : $".{x}")
                .ToArray();
        }

        if (options.ExcludedExtensions != null)
        {
            options.ExcludedExtensions = options.ExcludedExtensions.Select(x => x.Trim().ToLower())
                .Select(x => x.StartsWith('.') ? x : $".{x}")
                .ToArray();
        }
    }

    private async Task TriggerEvents(FileReference? before, FileReference? after, MediaLibraryFileEventType type)
    {
        var fileEvent = new MediaLibraryFileEvent()
        {
            Id = before?.Id ?? after?.Id, OldFile = before, NewFile = after, Type = type
        };
        foreach (var handler in _eventHandlers)
        {
            await handler.HandleEvent(fileEvent);
        }
    }

    private async Task TriggerEvents(FolderReference? before, FolderReference? after, MediaLibraryFolderEventType type)
    {
        var folderEvent = new MediaLibraryFolderEvent()
        {
            Id = before?.Id ?? after?.Id, OldFolder = before, NewFolder = after, Type = type
        };
        foreach (var handler in _eventHandlers)
        {
            await handler.HandleEvent(folderEvent);
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