using Microsoft.EntityFrameworkCore;
using NewHeap.Media.FileStructureStorage.SqlServer.Entities;
using NewHeap.Media.Modules;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NewHeap.Media.FileStructureStorage.SqlServer;

internal partial class SqlServerFileStructureStorage : IFileStructureStorage
{
    private readonly FileStructureDbContext _dbContext;

    private static Dictionary<string, Action<FileReference, object>>? _fileReferenceAccessors = null;

    [GeneratedRegex(@"\/\/+")]
    private static partial Regex DuplicatedSlashesRegex();

    private static Dictionary<string, Action<FileReference, object>> FileReferenceAccessors
    {
        get
        {
            if (_fileReferenceAccessors != null)
            {
                return _fileReferenceAccessors;
            }

            _fileReferenceAccessors = new Dictionary<string, Action<FileReference, object>>();
            foreach (var property in typeof(FileReference).GetProperties())
            {
                _fileReferenceAccessors.Add(property.Name.ToLower(), property.SetValue);
            }

            return _fileReferenceAccessors;
        }
    }

    public SqlServerFileStructureStorage(FileStructureDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<FileReference> CreateFile(FileModel model, Guid id)
    {
        model.Path = NormalizePath(model.Path);

        if (string.IsNullOrWhiteSpace(model.Name))
        {
            return null!;
        }

        string? metaDataJson = null;
        if (model.MetaData != null)
        {
            metaDataJson = JsonSerializer.Serialize(model.MetaData);
        }

        var entity = new FileEntity
        {
            Id = id,
            Path = model.Path,
            Name = model.Name,
            Creator = model.Creator,
            Description = model.Description,
            Title = model.Title,
            Tags = model.Tags?.ToList() ?? [],
            AltText = model.AltText,
            MetaData = metaDataJson
        };

        if (entity.Path != NhMediaValues.DirectorySeparator)
        {
            var parts = entity.Path.Split(NhMediaValues.DirectorySeparator, StringSplitOptions.RemoveEmptyEntries);

            var p = NhMediaValues.DirectorySeparator;
            foreach (var part in parts)
            {
                await CreateFolder(p, part);

                p += part.Trim(NhMediaValues.DirectorySeparator[0]) + NhMediaValues.DirectorySeparator;
            }
        }

        _dbContext.Files.Add(entity);
        await _dbContext.SaveChangesAsync();

        return new FileReference
        {
            Id = entity.Id,
            Name = entity.Name,
            Tags = entity.Tags,
            AltText = entity.AltText,
            Description = entity.Description,
            Creator = entity.Creator,
            Title = entity.Title,
            MetaData = string.IsNullOrEmpty(entity.MetaData)
                ? null
                : JsonSerializer.Deserialize<Dictionary<string, object>>(entity.MetaData),
            Folder = await GetFolderReference(entity.Path)
        };
    }

    public async Task<FileReference?> UpdateFile(Guid id, FileModel model)
    {
        model.Path = NormalizePath(model.Path);
        var entity = await _dbContext.Files.FirstOrDefaultAsync(x => x.Id == id);

        if (entity == null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(model.Name))
        {
            return null;
        }

        var sep = model.Path.LastIndexOf(NhMediaValues.DirectorySeparator, StringComparison.Ordinal);
        var folderPath = model.Path[..sep];
        var folderName = model.Path[sep..];

        var existingFolder =
            await _dbContext.Folders.FirstOrDefaultAsync(x => x.Path == folderName && x.Name == folderName);
        if (existingFolder == null)
        {
            await CreateFolder(folderPath, folderName);
        }

        entity.Path = model.Path;
        entity.Name = model.Name;
        entity.Creator = model.Creator;
        entity.Description = model.Description;
        entity.Title = model.Title;
        entity.Tags = model.Tags?.ToList() ?? [];
        entity.AltText = model.AltText;
        entity.MetaData = JsonSerializer.Serialize(model.MetaData);

        await _dbContext.SaveChangesAsync();
        return new FileReference
        {
            Id = entity.Id,
            Name = entity.Name,
            Tags = entity.Tags,
            AltText = entity.AltText,
            Description = entity.Description,
            Creator = entity.Creator,
            Title = entity.Title,
            MetaData = string.IsNullOrEmpty(entity.MetaData)
                ? null
                : JsonSerializer.Deserialize<Dictionary<string, object>>(entity.MetaData),
            Folder = await GetFolderReference(entity.Path),
        };
    }

    private string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return NhMediaValues.DirectorySeparator;
        }


        path = path.Replace('\\', NhMediaValues.DirectorySeparator[0]);
        path = DuplicatedSlashesRegex().Replace(path, NhMediaValues.DirectorySeparator);
        if (!path.StartsWith(NhMediaValues.DirectorySeparator))
        {
            path = NhMediaValues.DirectorySeparator + path;
        }

        if (path.EndsWith(NhMediaValues.DirectorySeparator))
        {
            path = path[..^1];
        }

        return path;
    }

    public async Task<FolderReference> CreateFolder(string? path, string folderName)
    {
        path = NormalizePath(path);

        var exists = await _dbContext.Folders.AnyAsync(x => x.Path == path && x.Name == folderName);
        if (exists)
        {
            return await GetFolderReference(MediaLibraryPath.Combine(path, folderName));
        }

        if (path != NhMediaValues.DirectorySeparator)
        {
            var parts = path.Split(NhMediaValues.DirectorySeparator);
            string? currentPath = null;
            foreach (var part in parts)
            {
                currentPath = NormalizePath(currentPath);
                var existing =
                    await _dbContext.Folders.FirstOrDefaultAsync(x => x.Path == currentPath && x.Name == part);
                if (existing == null)
                {
                    if (!string.IsNullOrWhiteSpace(part) && string.IsNullOrWhiteSpace(currentPath))
                    {
                        currentPath = NhMediaValues.DirectorySeparator;
                    }

                    

                    existing = new FolderEntity
                    {
                        Name = part,
                        Path = currentPath == NhMediaValues.DirectorySeparator ? "" : currentPath
                    };
                    if (!string.IsNullOrWhiteSpace(existing.Name))
                    {
                        _dbContext.Folders.Add(existing);
                    }
                }

                currentPath = MediaLibraryPath.Combine(currentPath, part);
            }
        }

        var folder = new FolderEntity { Name = folderName, Path = path };

        _dbContext.Folders.Add(folder);
        await _dbContext.SaveChangesAsync();

        return await GetFolderReference(MediaLibraryPath.Combine(path, folderName));
    }

    public async Task<bool> DeleteFolder(string? path, string folderName)
    {
        path = NormalizePath(path);

        var fullPath = MediaLibraryPath.Combine(path, folderName);

        var fileIds = await _dbContext.Files.Where(x => x.Path!.StartsWith(fullPath))
            .Select(x => x.Id)
            .ToListAsync();
        var count = fileIds.Count;
        count += await _dbContext.Localizations
            .Where(x => fileIds.Contains(x.EntityId) && x.TypeName == nameof(FileEntity)).ExecuteDeleteAsync();
        count += await _dbContext.Files.Where(x => x.Path!.StartsWith(fullPath)).ExecuteDeleteAsync();
        count += await _dbContext.Folders.Where(x => x.Path!.StartsWith(fullPath)).ExecuteDeleteAsync();
        count += await _dbContext.Folders.Where(x => x.Path == path && x.Name == folderName).ExecuteDeleteAsync();
        return count > 0;
    }

    public async Task<IEnumerable<FileReference>> GetFiles(string? path, string? language)
    {
        var content = await GetFolder(path, language);
        return content.Files;
    }

    public async Task<FolderReference> GetFolderReference(string? path)
    {
        path = NormalizePath(path);
        MediaLibraryPath.Split(path, out var folderPath, out var folderName);
        var id = await _dbContext.Folders
            .Where(x => x.Path == path && x.Name == folderName)
            .Select(x => (Guid?)x.Id)
            .FirstOrDefaultAsync();

        return new FolderReference { Id = id, Path = folderPath, Name = folderName, FullPath = path };
    }

    public async Task<FolderContents> GetFolder(string? path, string? language)
    {
        path = NormalizePath(path);
        var result = new FolderContents();
        var folders = await _dbContext.Folders.AsNoTracking()
            .Where(x => x.Path == path)
            .Where(x => !string.IsNullOrEmpty(x.Name))
            .ToArrayAsync();

        foreach (var folder in folders)
        {
            if (string.IsNullOrEmpty(folder.Name) || folder.Name == NhMediaValues.DirectorySeparator)
            {
                continue;
            }

            var folderReference = await GetFolderReference($"{folder.Path}/{folder.Name}");
            result.Folders.Add(folderReference);
        }

        var files = await _dbContext.Files.AsNoTracking()
            .Where(x => x.Path == path)
            .ToArrayAsync();

        foreach (var file in files)
        {
            var fileReference = new FileReference
            {
                Id = file.Id,
                Name = file.Name,
                Tags = file.Tags,
                AltText = file.AltText,
                Description = file.Description,
                Creator = file.Creator,
                Title = file.Title,
                MetaData = string.IsNullOrEmpty(file.MetaData)
                    ? null
                    : JsonSerializer.Deserialize<Dictionary<string, object>>(file.MetaData),
                Folder = await GetFolderReference(path),
            };
            result.Files.Add(fileReference);
            await ApplyLocalizations(fileReference, language);
        }

        return result;
    }

    public async Task<FileReference?> GetFile(string? path, string fileName, string? language)
    {
        path = NormalizePath(path);

        var file = await _dbContext.Files.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Path == path && x.Name == fileName);
        if (file == null)
        {
            return null;
        }

        var reference = new FileReference
        {
            Id = file.Id,
            Name = file.Name,
            Tags = file.Tags,
            AltText = file.AltText,
            Description = file.Description,
            Creator = file.Creator,
            Title = file.Title,
            MetaData = string.IsNullOrEmpty(file.MetaData)
                ? null
                : JsonSerializer.Deserialize<Dictionary<string, object>>(file.MetaData),
            Folder = await GetFolderReference(path),
        };

        await ApplyLocalizations(reference, language);
        return reference;
    }

    private async Task ApplyLocalizations(FileReference file, string? language)
    {
        if (string.IsNullOrEmpty(language))
        {
            return;
        }

        var localizations = await _dbContext.Localizations
            .Where(x => x.TypeName == nameof(FileEntity) && x.EntityId == file.Id && x.Language == language)
            .ToListAsync();
        foreach (var localization in localizations)
        {
            if (FileReferenceAccessors.TryGetValue(localization.PropertyName.ToLower(), out var setter))
            {
                setter(file, localization.Value);
            }
        }
    }

    public async Task<bool> DeleteFile(string? path, string filename)
    {
        path = NormalizePath(path);
        var fileQuery = _dbContext.Files.Where(x => x.Path == path && x.Name == filename);
        await _dbContext.Localizations
            .Where(x => fileQuery.Select(y => y.Id).Contains(x.EntityId) && x.TypeName == nameof(FileEntity))
            .ExecuteDeleteAsync();
        var count = await fileQuery.ExecuteDeleteAsync();
        return count > 0;
    }

    public async Task<IEnumerable<FileReference>> Search(string searchTerm, string? path, string? language,
        string[]? tags = null)
    {
        path = NormalizePath(path);
        var q = _dbContext.Files.AsNoTracking();
        if (!string.IsNullOrEmpty(path))
        {
            q = q.Where(x => x.Path!.StartsWith(path));
        }

        if (searchTerm?.Length > 0)
        {
            q = q.Where(x =>
                x.Name.Contains(searchTerm)
                || x.Tags.Any(y => y.Contains(searchTerm))
            );
            q = q.Where(x => x.Tags.Any(y => y.Contains(searchTerm)));
        }

        if (tags?.Length > 0)
        {
            q = q.Where(x => tags.All(y => x.Tags.Contains(y)));
        }

        var files = await q.ToListAsync();
        var result = new List<FileReference>();
        foreach (var file in files)
        {
            var reference = new FileReference
            {
                Id = file.Id,
                Name = file.Name,
                Tags = file.Tags,
                AltText = file.AltText,
                Description = file.Description,
                Creator = file.Creator,
                Title = file.Title,
                MetaData = string.IsNullOrEmpty(file.MetaData)
                    ? null
                    : JsonSerializer.Deserialize<Dictionary<string, object>>(file.MetaData),
                Folder = await GetFolderReference(file.Path),
            };
            result.Add(reference);
            await ApplyLocalizations(reference, language);
        }

        return result;
    }

    public async Task<bool> Localize(Guid entityId, string language, string propertyName, string? value)
    {
        var entity = await _dbContext.Localizations.FirstOrDefaultAsync(x =>
            x.TypeName == nameof(FileEntity)
            && x.EntityId == entityId
            && x.Language == language
            && x.PropertyName == propertyName
        );

        if (string.IsNullOrEmpty(value))
        {
            if (entity == null)
            {
                return false; // Doesn't exist, nothing to delete
            }

            _dbContext.Localizations.Remove(entity);
            await _dbContext.SaveChangesAsync();
            return true;
        }

        if (entity == null)
        {
            entity = new Localization
            {
                TypeName = nameof(FileEntity),
                EntityId = entityId,
                PropertyName = propertyName,
                Value = value,
                Language = language
            };
            _dbContext.Localizations.Add(entity);
        }
        else
        {
            entity.Value = value;
        }

        await _dbContext.SaveChangesAsync();
        return true;
    }

    public async Task<bool> UpdateTags(string path, string fileName, IEnumerable<string> tags)
    {
        path = NormalizePath(path);
        var file = await _dbContext.Files.FirstOrDefaultAsync(x => x.Path == path && x.Name == fileName);
        if (file == null)
        {
            return false;
        }

        file.Tags = tags.ToList();
        await _dbContext.SaveChangesAsync();
        return true;
    }

    public async Task<FileReference?> GetById(Guid id)
    {
        var entity = await _dbContext.Files.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (entity == null)
        {
            return null;
        }

        return new FileReference
        {
            Id = entity.Id,
            Name = entity.Name,
            Tags = entity.Tags,
            AltText = entity.AltText,
            Description = entity.Description,
            Creator = entity.Creator,
            Title = entity.Title,
            MetaData = string.IsNullOrEmpty(entity.MetaData)
                ? null
                : JsonSerializer.Deserialize<Dictionary<string, object>>(entity.MetaData),
            Folder = await GetFolderReference(entity.Path),
        };
    }

    public async Task<FolderReference?> MoveFolder(string? path, string folderName, string newPath, string newName)
    {
        path = NormalizePath(path);
        newPath = NormalizePath(newPath);

        var folder = await _dbContext.Folders.FirstOrDefaultAsync(x => x.Path == path && x.Name == folderName);
        if (folder == null)
        {
            return null;
        }

        folder.Path = newPath;
        folder.Name = newName;
        await _dbContext.SaveChangesAsync();
        return await GetFolderReference($"{newPath}/{newName}");
    }
}