using NewHeap.Media.EventHandlers;
using NewHeap.Media.Models;
using NewHeap.Media.Modules;
using NewHeap.Platform.Common.Models;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace NewHeap.Media;

[SuppressMessage("ReSharper", "ClassWithVirtualMembersNeverInherited.Global")]
[SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
public class MediaLibraryService : IMediaLibraryService
{
    private readonly IEnumerable<IHandleMediaLibraryEvent> _eventHandlers;
    private readonly IThumbnailService _thumbnailService;
    private readonly IFileStructureStorage _fileStructureStorage;
    private readonly IMediaStorage _fileStorage;
    private readonly IAuthorizationModule _authorizationModule;
    private readonly ILogger<MediaLibraryService> _logger;

    public MediaLibraryService(
        [Optional] IEnumerable<IHandleMediaLibraryEvent> eventHandlers,
        IThumbnailService thumbnailService,
        IFileStructureStorage fileStructureStorage,
        IMediaStorage fileStorage,
        IAuthorizationModule authorizationModule,
        ILogger<MediaLibraryService> logger
    )
    {
        _eventHandlers = eventHandlers;
        _thumbnailService = thumbnailService;
        _fileStructureStorage = fileStructureStorage;
        _fileStorage = fileStorage;
        _authorizationModule = authorizationModule;
        _logger = logger;
    }

    public virtual Task<TaskResult> LocalizeFieldAsync(Guid fileReferenceId, string propertyName, string language,
        string value)
    {
        return _fileStructureStorage.LocalizeAsync(fileReferenceId, language, propertyName, value);
    }

    public virtual async Task<TaskResult> UpdateFileTagsAsync(string? path, string fileName, IEnumerable<string> tags)
    {
        var current = await _fileStructureStorage.GetFileAsync(path, fileName, null);

        if (current == null)
        {
            return TaskResult.Failed("File not found");
        }

        path ??= "";
        var newRef = current.Copy(x => { x.Tags = tags; });

        await TriggerEvents(current, newRef, MediaLibraryFileEventType.Updating);
        var result = await _fileStructureStorage.UpdateTagsAsync(path, fileName, tags);
        await TriggerEvents(current, await _fileStructureStorage.GetFileAsync(path, fileName, null),
            MediaLibraryFileEventType.Updated);
        return result;
    }

    
    public virtual async Task<TaskResult<FileReference>> MoveFileAsync(Guid id, string newPath)
    {
        var fileRef = await _fileStructureStorage.GetByIdAsync(id);
        await EnsureAuthorized(fileRef?.Folder.FullPath ?? "", fileRef?.Name ?? "", null, ActionType.Update);
        
        if (fileRef == null)
        {
            return TaskResult<FileReference>.Failed("File not found");
        }
        
        MediaLibraryPath.Split(newPath, out var folderFullPath, out var fileName);

        // Storage keeps the file's name and places it in folderFullPath, so that is the destination.
        var destination = await EnsureFileDestinationAsync(fileRef, folderFullPath, fileRef.Name);
        if (!destination.Success)
        {
            return TaskResult<FileReference>.Failed(destination);
        }

        MediaLibraryPath.Split(folderFullPath ?? "", out var folderPath, out var folderName);

        var newRef = fileRef.Copy(x =>
        {
            x.Name = fileName;
            x.Folder = new FolderReference { Name = folderName, Path = folderPath, FullPath = folderFullPath ?? "/" };
        });
        await TriggerEvents(fileRef, newRef, MediaLibraryFileEventType.Updating);
        var result = await _fileStructureStorage.UpdateFileAsync(fileRef.Id, new FileModel()
        {
            Tags = fileRef.Tags.ToArray(),
            Name = fileRef.Name,
            Path = folderFullPath,
            MetaData = fileRef.MetaData,
            Description = fileRef.Description,
            AltText = fileRef.AltText,
            Title = fileRef.Title,
            Creator = fileRef.Creator,
        });
        if (result.Success)
        {
            await TriggerEvents(fileRef, newRef, MediaLibraryFileEventType.Updated);
        }

        return result;
    } 

    public virtual async Task<TaskResult> RenameFileAsync(Guid id, string newFilename)
    {
        var fileRef = await _fileStructureStorage.GetByIdAsync(id);
        await EnsureAuthorized(fileRef?.Folder.FullPath ?? "", fileRef?.Name ?? "", null, ActionType.Update);
        
        if (fileRef == null)
        {
            return TaskResult.Failed("File not found");
        }

        var destination = await EnsureFileDestinationAsync(fileRef, fileRef.Folder.FullPath, newFilename);
        if (!destination.Success)
        {
            return destination;
        }

        var newRef = fileRef.Copy(x =>
        {
            x.Name = newFilename;
            x.Folder = new FolderReference { Name = fileRef.Folder.Name, Path = fileRef.Folder.Path, FullPath = fileRef.Folder.FullPath };
        });
        await TriggerEvents(fileRef, newRef, MediaLibraryFileEventType.Updating);

        var result = await _fileStructureStorage.UpdateFileAsync(fileRef.Id, new FileModel()
        {
            Tags = fileRef.Tags.ToArray(),
            Name = newFilename,
            Path = fileRef.Folder.FullPath,
            MetaData = fileRef.MetaData,
            Description = fileRef.Description,
            AltText = fileRef.AltText,
            Title = fileRef.Title,
            Creator = fileRef.Creator,
        });
        if (result.Success)
        {
            await TriggerEvents(fileRef, newRef, MediaLibraryFileEventType.Updating);
        }

        return result;
    }
    

    public virtual async Task<TaskResult> RenameFileAsync(string path, string filename, string newPath,
        string newFilename)
    {
        await EnsureAuthorized(path, filename, null, ActionType.Update);

        filename = filename.Replace(NhMediaValues.DirectorySeparator, string.Empty);
        newFilename = newFilename.Replace(NhMediaValues.DirectorySeparator, string.Empty);

        var fileRef = await _fileStructureStorage.GetFileAsync(path, filename, null);
        if (fileRef == null)
        {
            return TaskResult.Failed("File not found");
        }

        var destination = await EnsureFileDestinationAsync(fileRef, newPath, newFilename);
        if (!destination.Success)
        {
            return destination;
        }

        var newRef = fileRef.Copy(x =>
        {
            MediaLibraryPath.Split(path, out var folderPath, out var folderName);
            x.Name = newFilename;
            x.Folder = new FolderReference { Name = folderName, Path = folderPath, FullPath = path };
        });
        await TriggerEvents(fileRef, newRef, MediaLibraryFileEventType.Updating);

        var result = await _fileStructureStorage.UpdateFileAsync(fileRef.Id, new FileModel()
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
        if (result.Success)
        {
            await TriggerEvents(fileRef, newRef, MediaLibraryFileEventType.Updated);
        }

        return result;
    }

    public virtual async Task<TaskResult<FileReference>> CreateFileAsync(FileModel model, Stream file)
    {
        try
        {
            await EnsureAuthorized(model.Path, model.Name, null, ActionType.Create);
            model.Name = model.Name!.Replace(NhMediaValues.DirectorySeparator, string.Empty);

            MediaLibraryPath.Split(model.Path ?? NhMediaValues.DirectorySeparator, out var folderPath,
                out var folderName);
            var newRef = new FileReference()
            {
                Name = model.Name!,
                Folder = new FolderReference()
                {
                    Path = folderPath, Name = folderName, FullPath = model.Path ?? "/"
                },
            };

            await TriggerEvents(null, newRef, MediaLibraryFileEventType.Adding);

            var fileId = await _fileStorage.SaveFileAsync(file);
            var fileRef = await _fileStructureStorage.CreateFileAsync(model, fileId);
            if (!fileRef.Success)
            {
                await _fileStorage.DeleteAsync(fileId);
                return fileRef;
            }

            await TriggerEvents(null, fileRef.Data, MediaLibraryFileEventType.Added);
            return fileRef;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to create a media file.");
            return TaskResult<FileReference>.Failed("media.file.create-failed");
        }
    }

    public virtual async Task<TaskResult<FolderReference>> CreateFolderAsync(string? path, string folderName)
    {
        await EnsureAuthorized(path, null, null, ActionType.Create);

        folderName = folderName.Replace(NhMediaValues.DirectorySeparator, string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(folderName))
        {
            return TaskResult<FolderReference>.Failed("New folder name cannot be empty or whitespace.");
        }

        var newRef = new FolderReference()
        {
            Name = folderName,
            Path = path ?? NhMediaValues.DirectorySeparator,
            FullPath = Path.Combine(path ?? NhMediaValues.DirectorySeparator, folderName),
        };
        await TriggerEvents(null, newRef, MediaLibraryFolderEventType.Adding);
        var folderRef = await _fileStructureStorage.CreateFolderAsync(path, folderName);
        await TriggerEvents(null, folderRef, MediaLibraryFolderEventType.Added);
        return folderRef;
    }

    public virtual async Task<TaskResult<FolderReference>> UpdateFolderAsync(string? path, string folderName, string? newPath,
        string newName)
    {
        var sourcePath = MediaLibraryPath.Combine(path, folderName);
        await EnsureAuthorized(sourcePath, null, null, ActionType.Update);

        newName = newName.Replace(NhMediaValues.DirectorySeparator, string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(newName))
        {
            return TaskResult<FolderReference>.Failed("New folder name cannot be empty or whitespace.");
        }

        // A folder that moves or gets another name is authorized as a folder created in its destination
        // parent, the check a direct folder create there passes.
        var destinationParent = MediaLibraryPath.Combine(newPath ?? NhMediaValues.DirectorySeparator);
        var destinationPath = MediaLibraryPath.Combine(destinationParent, newName);
        var changesLocation = !string.Equals(destinationPath, sourcePath, StringComparison.Ordinal);
        if (changesLocation)
        {
            await EnsureAuthorized(destinationParent, null, null, ActionType.Create);
        }

        var reference = await _fileStructureStorage.GetFolderReferenceAsync(sourcePath);
        if (changesLocation)
        {
            var existing = await _fileStructureStorage.GetFolderReferenceAsync(destinationPath);
            if (existing?.Id is not null && existing.Id != reference?.Id)
            {
                return TaskResult<FolderReference>.Failed(nameof(newName), NhMediaErrorCodes.FolderNameExists);
            }
        }

        var newRef = reference.Copy(x =>
        {
            x.Path = newPath;
            x.Name = newName;
        });

        await TriggerEvents(reference, newRef, MediaLibraryFolderEventType.Updated);
        newRef = await _fileStructureStorage.MoveFolderAsync(path, folderName,
            newPath ?? NhMediaValues.DirectorySeparator,
            newName);
        await TriggerEvents(null, newRef, MediaLibraryFolderEventType.Updated);

        if (newRef == null)
        {
            return TaskResult<FolderReference>.Failed("Folder not found");
        }

        return newRef;
    }

    public virtual async Task<TaskResult<FileReference>> GetFileAsync(string? path, string filename, string? language = null)
    {
        await EnsureAuthorized(path, filename, null, ActionType.Read);
        var fileRef = await _fileStructureStorage.GetFileAsync(path, filename, language);

        if (fileRef == null)
        {
            return TaskResult<FileReference>.Failed("File not found");
        }

        fileRef.Thumbnail = await _thumbnailService.GetThumbnailAsync(fileRef.Id);
        return fileRef;
    }

    public virtual async Task<TaskResult<FileReference>> GetFileAsync(Guid id)
    {
        var reference = await _fileStructureStorage.GetByIdAsync(id);
        if (reference != null)
        {
            reference.Thumbnail = await _thumbnailService.GetThumbnailAsync(reference.Id);
        }

        if (reference == null)
        {
            return TaskResult<FileReference>.Failed("File not found");
        }

        return reference;
    }

    public virtual async Task<IReadOnlyDictionary<Guid, FileReference>> GetFilesAsync(IEnumerable<Guid> ids)
    {
        var references = await _fileStructureStorage.GetByIdsAsync(ids);
        foreach (var reference in references.Values)
        {
            reference.Thumbnail = await _thumbnailService.GetThumbnailAsync(reference.Id);
        }

        return references;
    }

    public virtual async Task<DisposableTaskResult<Stream>> DownloadFileAsync(string? path, string fileName)
    {
        await EnsureAuthorized(path, fileName, null, ActionType.Read);
        var fileRef = await _fileStructureStorage.GetFileAsync(path, fileName, null);
        if (fileRef == null)
        {
            return DisposableTaskResult<Stream>.Failed("File not found");
        }

        var stream = await _fileStorage.GetFileAsync(fileRef.Id);
        if (stream == null)
        {
            return DisposableTaskResult<Stream>.Failed("File not found");
        }

        return stream;
    }

    public virtual async Task<DisposableTaskResult<Stream>> DownloadFileAsync(Guid id)
    {
        var fileRef = await _fileStructureStorage.GetByIdAsync(id);
        if (fileRef == null)
        {
            return DisposableTaskResult<Stream>.Failed("File not found");
        }

        var stream = await _fileStorage.GetFileAsync(fileRef.Id);

        if (stream == null)
        {
            return DisposableTaskResult<Stream>.Failed("File not found");
        }

        return stream;
    }

    public virtual async Task<FolderContents> GetFolder(string? path, string? language, FileGetOptions? sortOptions = null)
    {
        await EnsureAuthorized(path, null, language, ActionType.Read);
        var folder = await _fileStructureStorage.GetFolderAsync(path, language, sortOptions);
        foreach (var file in folder.Files)
        {
            file.Thumbnail = await _thumbnailService.GetThumbnailAsync(file.Id);
        }

        return folder;
    }

    public virtual async Task<TaskResult> UpdateFileAsync(string? path, string fileName, Stream file)
    {
        await EnsureAuthorized(path, fileName, null, ActionType.Update);

        fileName = fileName.Replace(NhMediaValues.DirectorySeparator, string.Empty);

        var fileRef = await _fileStructureStorage.GetFileAsync(path, fileName, null);
        if (fileRef == null)
        {
            return TaskResult.Failed("File not found");
        }

        await TriggerEvents(fileRef, fileRef, MediaLibraryFileEventType.Updating);
        var result = await _fileStorage.UpdateFileAsync(file, fileRef.Id);
        if (result.Success)
        {
            await TriggerEvents(fileRef, fileRef, MediaLibraryFileEventType.BinaryUpdated);
        }

        return result;
    }

    public virtual async Task<TaskResult> UpdateFileAsync(Guid id, FileModel model)
    {
        var reference = await _fileStructureStorage.GetByIdAsync(id);

        if (reference == null)
        {
            return TaskResult.Failed("File not found");
        }

        await EnsureAuthorized(MediaLibraryPath.Combine(reference.Folder.Path, reference.Folder.Name), reference.Name,
            null,
            ActionType.Update);

        model.Name = model.Name?.Replace(NhMediaValues.DirectorySeparator, string.Empty);

        if (string.IsNullOrWhiteSpace(model.Name))
        {
            return TaskResult.Failed("Name is required");
        }

        var destination = await EnsureFileDestinationAsync(reference, model.Path, model.Name);
        if (!destination.Success)
        {
            return destination;
        }

        var folder = await _fileStructureStorage.GetFolderReferenceAsync(model.Path);
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
        var result = await _fileStructureStorage.UpdateFileAsync(reference.Id, model);
        if (result.Success)
        {
            await TriggerEvents(reference, await _fileStructureStorage.GetByIdAsync(id),
                MediaLibraryFileEventType.Updated);
        }

        return result;
    }

    public virtual async Task<TaskResult> DeleteFolderAsync(string? path, string folderName)
    {
        folderName = folderName.Replace(NhMediaValues.DirectorySeparator, string.Empty);
        var folderPath = MediaLibraryPath.Combine(path, folderName);
        await EnsureAuthorized(folderPath, null, null, ActionType.Delete);

        var files = (await _fileStructureStorage.SearchAsync(
            "",
            folderPath,
            new SearchOptions { PageIndex = 0, PageSize = int.MaxValue, IncludeTotalCount = false })).Results.ToList();

        var folder = await _fileStructureStorage.GetFolderReferenceAsync(folderPath);

        await TriggerEvents(folder, null, MediaLibraryFolderEventType.Removing);

        var deleted = await _fileStructureStorage.DeleteFolderAsync(path, folderName);
        if (!deleted)
        {
            return TaskResult.Failed("Failed to delete folder");
        }

        await TriggerEvents(folder, null, MediaLibraryFolderEventType.Removed);

        foreach (var file in files)
        {
            await TriggerEvents(file, null, MediaLibraryFileEventType.Removing);
        }

        foreach (var file in files)
        {
            await _fileStorage.DeleteAsync(file.Id);
        }

        foreach (var file in files)
        {
            await TriggerEvents(file, null, MediaLibraryFileEventType.Removed);
        }

        return TaskResult.Succeeded();
    }

    public virtual async Task<TaskResult> DeleteFileAsync(string? path, string fileName)
    {
        fileName = fileName.Replace(NhMediaValues.DirectorySeparator, string.Empty);
        await EnsureAuthorized(path, fileName, null, ActionType.Delete);
        var fileRef = await _fileStructureStorage.GetFileAsync(path, fileName, null);
        if (fileRef == null)
        {
            return TaskResult.Failed("File not found");
        }

        await TriggerEvents(fileRef, null, MediaLibraryFileEventType.Removing);
        await _fileStorage.DeleteAsync(fileRef.Id);
        await _fileStructureStorage.DeleteFileAsync(path, fileName);
        await TriggerEvents(fileRef, null, MediaLibraryFileEventType.Removed);
        return TaskResult.Succeeded();
    }

    public virtual async Task<SearchResults> SearchAsync(string? path, string searchTerm, SearchOptions options)
    {
        await EnsureAuthorized(path, null, options.Language, ActionType.Read);

        NormalizeOptions(options);

        var searchResults = await _fileStructureStorage.SearchAsync(searchTerm, path, options);

        foreach (var file in searchResults.Results)
        {
            file.Thumbnail = await _thumbnailService.GetThumbnailAsync(file.Id);
        }

        return searchResults;
    }

    protected void NormalizeOptions(SearchOptions options)
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

    protected async Task TriggerEvents(FileReference? before, FileReference? after, MediaLibraryFileEventType type)
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

    protected async Task TriggerEvents(FolderReference? before, FolderReference? after, MediaLibraryFolderEventType type)
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

    /// <summary>
    /// Authorizes the destination of a file that moves to another folder or gets another name as a file
    /// created there, the check a direct upload to that place passes, and refuses a destination folder
    /// that already holds another file with that name. A file that keeps its folder and name needs no
    /// destination check.
    /// </summary>
    /// <remarks>
    /// The existing-name lookup follows the storage's own path and name lookup, so it refuses exactly the
    /// names a later path-based read, overwrite or delete could no longer tell apart.
    /// </remarks>
    private async Task<TaskResult> EnsureFileDestinationAsync(FileReference file, string? destinationPath,
        string destinationName)
    {
        var sourcePath = MediaLibraryPath.Combine(file.Folder.FullPath);
        var targetPath = MediaLibraryPath.Combine(destinationPath);
        if (string.Equals(targetPath, sourcePath, StringComparison.Ordinal)
            && string.Equals(destinationName, file.Name, StringComparison.Ordinal))
        {
            return TaskResult.Succeeded();
        }

        await EnsureAuthorized(targetPath, destinationName, null, ActionType.Create);

        var existing = await _fileStructureStorage.GetFileAsync(targetPath, destinationName, null);
        if (existing != null && existing.Id != file.Id)
        {
            return TaskResult.Failed(nameof(FileModel.Name), NhMediaErrorCodes.FileNameExists);
        }

        return TaskResult.Succeeded();
    }

    protected async Task EnsureAuthorized(
        string? path,
        string? filename,
        string? language,
        ActionType action)
    {
        var context = new AuthorizationContext
        {
            Path = path, FileName = filename, Language = language, Action = action
        };
        await _authorizationModule.IsAuthorizedAsync(context);
        if (!context.Authorized)
        {
            throw new UnauthorizedAccessException();
        }
    }
}
