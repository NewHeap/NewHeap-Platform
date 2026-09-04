using Microsoft.EntityFrameworkCore;
using NewHeap.Media.FileStructureStorage.SqlServer.Entities;
using NewHeap.Media.FileStructureStorage.SqlServer;
using NewHeap.Media.Models;
using NewHeap.Media.Modules;
using NewHeap.Platform.Common.Models;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
// ReSharper disable EntityFramework.ClientSideDbFunctionCall

namespace NewHeap.Media.FileStructureStorage;

/// <summary>
/// Provider-neutral relational implementation of the media file-structure contract.
/// Provider packages supply the lookup-hash semantics and EF Core model configuration.
/// </summary>
public abstract partial class RelationalFileStructureStorage : IFileStructureStorage
{
    private static readonly IReadOnlyDictionary<string, Action<FileReference, object?>> FileReferenceAccessors =
        typeof(FileReference)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .ToDictionary(
                x => x.Name,
                x => (Action<FileReference, object?>)x.SetValue,
                StringComparer.OrdinalIgnoreCase);

    private readonly FileStructureDbContext _dbContext;

    protected RelationalFileStructureStorage(FileStructureDbContext dbContext)
    {
        _dbContext = dbContext;
    }


    public async Task<TaskResult<FileReference>> CreateFileAsync(FileModel model, Guid id)
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

        if (string.IsNullOrWhiteSpace(model.Name))
        {
            return TaskResult<FileReference>.Failed("Name is required");
        }

        var validation = ValidateLengths(model);
        if (!validation.Success)
        {
            return TaskResult<FileReference>.Failed(validation);
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
                await CreateFolderAsync(p, part);

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
            Folder = await GetFolderReferenceAsync(entity.Path),
            CreationDateTime = entity.CreationDateTime
        };
    }

    public async Task<TaskResult<FileReference>> UpdateFileAsync(Guid id, FileModel model)
    {
        model.Path = NormalizePath(model.Path);
        var entity = await _dbContext.Files.FirstOrDefaultAsync(x => x.Id == id);

        if (entity == null)
        {
            return TaskResult<FileReference>.Failed("File not found");
        }

        if (string.IsNullOrWhiteSpace(model.Name))
        {
            return TaskResult<FileReference>.Failed("Name is required");
        }

        var validation = ValidateLengths(model);
        if (!validation.Success)
        {
            return TaskResult<FileReference>.Failed(validation);
        }

        var sep = model.Path.LastIndexOf(NhMediaValues.DirectorySeparator, StringComparison.Ordinal);
        if (sep < 0)
        {
            sep = 0;
        }

        var folderPath = model.Path[..sep];
        var folderName = model.Path[sep..];

        var existingFolder =
            await WhereFolderPathAndName(_dbContext.Folders, folderName, folderName).FirstOrDefaultAsync();
        if (existingFolder == null)
        {
            await CreateFolderAsync(folderPath, folderName);
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
            Folder = await GetFolderReferenceAsync(entity.Path),
            CreationDateTime = entity.CreationDateTime
        };
    }

    public async Task<FolderReference> CreateFolderAsync(string? path, string folderName)
    {
        path = NormalizePath(path);

        folderName = folderName.Replace(NhMediaValues.DirectorySeparator, string.Empty);

        var exists = await WhereFolderPathAndName(_dbContext.Folders, path, folderName).AnyAsync();
        if (exists)
        {
            return await GetFolderReferenceAsync(MediaLibraryPath.Combine(path, folderName));
        }

        if (path != NhMediaValues.DirectorySeparator)
        {
            var parts = path.Split(NhMediaValues.DirectorySeparator);
            string? currentPath = null;
            foreach (var part in parts)
            {
                currentPath = NormalizePath(currentPath);
                var existing =
                    await WhereFolderPathAndName(_dbContext.Folders, currentPath, part).FirstOrDefaultAsync();
                if (existing == null)
                {
                    if (!string.IsNullOrWhiteSpace(part) && string.IsNullOrWhiteSpace(currentPath))
                    {
                        currentPath = NhMediaValues.DirectorySeparator;
                    }


                    existing = new FolderEntity
                    {
                        Name = part, Path = currentPath == NhMediaValues.DirectorySeparator ? "" : currentPath
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

        return await GetFolderReferenceAsync(MediaLibraryPath.Combine(path, folderName));
    }

    public async Task<bool> DeleteFolderAsync(string? path, string folderName)
    {
        path = NormalizePath(path);

        var fullPath = MediaLibraryPath.Combine(path, folderName);
        var filesInFolder = _dbContext.Files.WhereFilesInFolderTree(fullPath);
        var fileIdsInFolder = filesInFolder.Select(x => x.Id);

        var count = await _dbContext.Localizations
            .Where(x => fileIdsInFolder.Contains(x.EntityId) && x.TypeName == nameof(FileEntity))
            .ExecuteDeleteAsync();

        var childPathPrefix = DbSetExtensions.GetChildPathPrefix(fullPath);

        count += await filesInFolder.ExecuteDeleteAsync();
        count += await _dbContext.Folders
            .Where(x => x.Path == fullPath || (x.Path != null && x.Path.StartsWith(childPathPrefix)))
            .ExecuteDeleteAsync();
        count += await WhereFolderPathAndName(_dbContext.Folders, path, folderName).ExecuteDeleteAsync();
        return count > 0;
    }

    public async Task<IEnumerable<FileReference>> GetFilesAsync(string? path, string? language,
        FileGetOptions? sortOptions)
    {
        var content = await GetFolderAsync(path, language, sortOptions);
        return content.Files;
    }

    public async Task<FolderReference> GetFolderReferenceAsync(string? path)
    {
        path = NormalizePath(path);
        MediaLibraryPath.Split(path, out var folderPath, out var folderName);
        var id = await WhereFolderPathAndName(_dbContext.Folders, path, folderName)
            .Select(x => (Guid?)x.Id)
            .FirstOrDefaultAsync();

        return new FolderReference { Id = id, Path = folderPath, Name = folderName, FullPath = path };
    }

    public async Task<FolderContents> GetFolderAsync(string? path, string? language, FileGetOptions? sortOptions)
    {
        path = NormalizePath(path);
        var result = new FolderContents();

        var folders = await WhereFoldersInPath(_dbContext.Folders.AsNoTracking(), path)
            .Where(x => !string.IsNullOrEmpty(x.Name))
            .Select(x => new { x.Id, x.Path, x.Name })
            .ToArrayAsync();


        foreach (var folder in folders)
        {
            if (string.IsNullOrEmpty(folder.Name) || folder.Name == NhMediaValues.DirectorySeparator)
            {
                continue;
            }

            result.Folders.Add(new FolderReference
            {
                Id = folder.Id,
                Path = folder.Path,
                Name = folder.Name,
                FullPath = MediaLibraryPath.Combine(folder.Path, folder.Name)
            });
        }

        var q = WhereFilesInPath(_dbContext.Files.AsNoTracking(), path);


        q = ProcessOrderBy(sortOptions, q);

        var totalCount = await q.LongCountAsync();
        result.FileCount = totalCount;
        
        if (sortOptions?.PageSize != null)
        {
            var pageSize = Math.Max(sortOptions.PageSize.Value, 0);
            var page = Math.Max(sortOptions.Page, 0);
            var skip = Math.Min(page * pageSize, int.MaxValue);
            q = q.Skip(skip).Take(pageSize);
        }

        var files = await q.AsFileReferenceRow().ToArrayAsync();
        var currentFolder = await GetFolderReferenceAsync(path);

        foreach (var file in files)
        {
            result.Files.Add(CreateFileReference(file, currentFolder));
        }

        await ApplyLocalizations(result.Files, language);
        return result;
    }

    public async Task<FileReference?> GetFileAsync(string? path, string fileName, string? language)
    {
        path = NormalizePath(path);

        var file = await WhereFilePathAndName(_dbContext.Files.AsNoTracking(), path, fileName)
            .FirstOrDefaultAsync();
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
            Folder = await GetFolderReferenceAsync(path),
            CreationDateTime = file.CreationDateTime
        };

        await ApplyLocalizations([reference], language);
        return reference;
    }

    public async Task<TaskResult> DeleteFileAsync(string? path, string filename)
    {
        path = NormalizePath(path);
        var fileQuery = WhereFilePathAndName(_dbContext.Files, path, filename);
        await _dbContext.Localizations
            .Where(x => fileQuery.Select(y => y.Id).Contains(x.EntityId) && x.TypeName == nameof(FileEntity))
            .ExecuteDeleteAsync();
        var count = await fileQuery.ExecuteDeleteAsync();
        return count > 0 ? TaskResult.Succeeded() : TaskResult.Failed("Localization not found");
    }

    public async Task<SearchResults> SearchAsync(string? searchTerm, string? path, SearchOptions options)
    {
        options.PageSize = Math.Max(options.PageSize, 10);
        options.PageIndex = Math.Max(options.PageIndex, 0);

        var results = new SearchResults { PageIndex = options.PageIndex, ItemsPerPage = options.PageSize };
        path = NormalizePath(path);
        var q = _dbContext.Files.AsNoTracking();
        if (!string.IsNullOrEmpty(path))
        {
            q = q.WhereFilesInFolderTree(path);
        }

        if (searchTerm?.Length > 0)
        {
            q = q.Where(x =>
                x.Name.Contains(searchTerm)
                || x.Tags.Any(y => y.Contains(searchTerm))
            );
        }

        if (options.Tags?.Length > 0)
        {
            q = q.Where(x => options.Tags.All(y => x.Tags.Contains(y)));
        }


        if (options.IncludedExtensions?.Length > 0)
        {
            q = q.Where(x => options.IncludedExtensions.Any(y => x.Name.EndsWith(y)));
        }

        if (options.ExcludedExtensions?.Length > 0)
        {
            q = q.Where(x => !options.ExcludedExtensions.Any(y => x.Name.EndsWith(y)));
        }

        if (options.IncludeTotalCount)
        {
            results.TotalCount = await q.LongCountAsync();
        }

        var skip = Math.Min((long)options.PageIndex * options.PageSize, int.MaxValue);
        q = q.Skip((int)skip).Take(options.PageSize);

        var files = await q.AsFileReferenceRow().ToListAsync();
        var result = new List<FileReference>();
        var folderReferences = new Dictionary<string, FolderReference>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var folderKey = file.Path ?? string.Empty;
            if (!folderReferences.TryGetValue(folderKey, out var folderReference))
            {
                folderReference = await GetFolderReferenceAsync(file.Path);
                folderReferences.Add(folderKey, folderReference);
            }

            result.Add(CreateFileReference(file, folderReference));
        }

        await ApplyLocalizations(result, options.Language);
        results.Results = result;
        return results;
    }

    public async Task<TaskResult> LocalizeAsync(Guid entityId, string language, string propertyName, string? value)
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
                return TaskResult.Failed("Localization not found");
            }

            _dbContext.Localizations.Remove(entity);
            await _dbContext.SaveChangesAsync();
            return TaskResult.Succeeded();
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
        return TaskResult.Succeeded();
    }

    public async Task<TaskResult> UpdateTagsAsync(string path, string fileName, IEnumerable<string> tags)
    {
        path = NormalizePath(path);
        var file = await WhereFilePathAndName(_dbContext.Files, path, fileName).FirstOrDefaultAsync();
        if (file == null)
        {
            return TaskResult.Failed("File not found");
        }

        file.Tags = tags.ToList();
        await _dbContext.SaveChangesAsync();
        return TaskResult.Succeeded();
    }

    public async Task<FileReference?> GetByIdAsync(Guid id)
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
            Folder = await GetFolderReferenceAsync(entity.Path),
            CreationDateTime = entity.CreationDateTime
        };
    }

    public async Task<FolderReference?> MoveFolderAsync(string? path, string folderName, string newPath, string newName)
    {
        path = NormalizePath(path);
        newPath = NormalizePath(newPath);

        var folder = await WhereFolderPathAndName(_dbContext.Folders, path, folderName).FirstOrDefaultAsync();
        if (folder == null)
        {
            return null;
        }

        folder.Path = newPath;
        folder.Name = newName;
        await _dbContext.SaveChangesAsync();
        return await GetFolderReferenceAsync($"{newPath}/{newName}");
    }

    [GeneratedRegex(@"\/\/+")]
    private static partial Regex DuplicatedSlashesRegex();
    
    private static FileReference CreateFileReference(FileReferenceRow file, FolderReference folder)
    {
        var metaData = string.IsNullOrEmpty(file.MetaData)
            ? null
            : JsonSerializer.Deserialize<Dictionary<string, object>>(file.MetaData);

        return new FileReference
        {
            Id = file.Id,
            Name = file.Name,
            Tags = file.Tags,
            AltText = file.AltText,
            Description = file.Description,
            Creator = file.Creator,
            Title = file.Title,
            MetaData = metaData,
            Folder = folder,
            CreationDateTime = file.CreationDateTime
        };
    }
    
    private static TaskResult ValidateLengths(FileModel model)
    {
        var result = new TaskResult();
        if (string.IsNullOrWhiteSpace(model.Name))
        {
            result.AddError("Name is required");
        }
        else
        {
            ValidateMaxLength(model.Name, 2000);
        }

        ValidateMaxLength(model.AltText, 100);
        ValidateMaxLength(model.Description, 500);
        ValidateMaxLength(model.Path, 10_000);
        ValidateMaxLength(model.Creator, 150);
        ValidateMaxLength(model.Title, 100);

        void ValidateMaxLength(string? value, int maxLength, [CallerArgumentExpression("value")] string property = "")
        {
            if (value?.Length > maxLength)
            {
                property = property.Split('.').Last();
                result.AddError(property, $"{property} cannot be more than {maxLength} characters");
            }
        }

        return result;
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

        if (path.Length == 1 && path == NhMediaValues.DirectorySeparator)
        {
            return path;
        }

        if (path.EndsWith(NhMediaValues.DirectorySeparator))
        {
            path = path[..^1];
        }

        return path;
    }

    protected virtual IQueryable<FileEntity> WhereFilesInPath(IQueryable<FileEntity> query, string? path)
    {
        return query.Where(x => x.Path == path);
    }

    protected virtual IQueryable<FolderEntity> WhereFoldersInPath(IQueryable<FolderEntity> query, string? path)
    {
        return query.Where(x => x.Path == path);
    }

    protected virtual IQueryable<FileEntity> WhereFilePathAndName(IQueryable<FileEntity> query, string? path,
        string? name)
    {
        return query.Where(x => x.Path == path && x.Name == name);
    }

    protected virtual IQueryable<FolderEntity> WhereFolderPathAndName(IQueryable<FolderEntity> query, string? path,
        string? name)
    {
        return query.Where(x => x.Path == path && x.Name == name);
    }

    private IQueryable<T> ProcessOrderBy<T>(FileGetOptions? sortInfo, IQueryable<T> queryable)
    {
        if (sortInfo?.OrderBy == null)
        {
            return queryable;
        }

        var type = typeof(T);
        var orderableProperties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(x => x.CustomAttributes.Any(y => y.AttributeType == typeof(OrderableAttribute)))
                .ToList()
            ;


        foreach (var orderBy in sortInfo.OrderBy)
        {
            var prop = orderableProperties.FirstOrDefault(x =>
                x.Name.Equals(orderBy.Key, StringComparison.InvariantCultureIgnoreCase));
            if (prop != null)
            {
                var parameter = Expression.Parameter(typeof(T));
                var propAccess = Expression.Property(parameter, prop);
                var cast = Expression.Convert(propAccess, typeof(object));
                var expression = Expression.Lambda<Func<T, object>>(cast, parameter);

                if (orderBy.Direction == Direction.Ascending)
                {
                    queryable = queryable.OrderBy(expression);
                }
                else
                {
                    queryable = queryable.OrderByDescending(expression);
                }
            }
        }

        return queryable;
    }

    private async Task ApplyLocalizations(IEnumerable<FileReference> files, string? language)
    {
        if (string.IsNullOrEmpty(language))
        {
            return;
        }

        var fileReferences = files.ToArray();
        if (fileReferences.Length == 0)
        {
            return;
        }

        var referencesById = fileReferences.ToLookup(x => x.Id);
        IEnumerable<Guid> fileIds = referencesById.Select(x => x.Key).ToArray();

        var localizations = await _dbContext.Localizations
            .AsNoTracking()
            .Where(x => x.TypeName == nameof(FileEntity) && fileIds.Contains(x.EntityId) && x.Language == language)
            .ToListAsync();
        foreach (var localization in localizations)
        {
            if (FileReferenceAccessors.TryGetValue(localization.PropertyName, out var setter))
            {
                foreach (var file in referencesById[localization.EntityId])
                {
                    setter(file, localization.Value);
                }
            }
        }
    }
}

internal sealed class FileReferenceRow
{
    public Guid Id { get; init; }
    public required string Name { get; init; }
    public string? Path { get; init; }
    public string? Title { get; init; }
    public string? Description { get; init; }
    public string? AltText { get; init; }
    public string? Creator { get; init; }
    public string? MetaData { get; init; }
    public List<string> Tags { get; init; } = [];
    public DateTimeOffset CreationDateTime { get; init; }
}

file static class DbSetExtensions
{
    public static IQueryable<FileEntity> WhereFilesInFolderTree(this IQueryable<FileEntity> query, string fullPath)
    {
        var childPathPrefix = GetChildPathPrefix(fullPath);
        return query.Where(x => x.Path == fullPath || (x.Path != null && x.Path.StartsWith(childPathPrefix)));
    }
    
    public static IQueryable<FileReferenceRow> AsFileReferenceRow(this IQueryable<FileEntity> query)
    {
        return query.Select(x => new FileReferenceRow
        {
            Id = x.Id,
            Path = x.Path,
            Name = x.Name,
            Tags = x.Tags,
            AltText = x.AltText,
            Description = x.Description,
            Creator = x.Creator,
            Title = x.Title,
            MetaData = x.MetaData,
            CreationDateTime = x.CreationDateTime
        });
    }
    
    
    public static string GetChildPathPrefix(string fullPath)
    {
        return fullPath.EndsWith(NhMediaValues.DirectorySeparator)
            ? fullPath
            : fullPath + NhMediaValues.DirectorySeparator;
    }
}
