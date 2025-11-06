using Microsoft.EntityFrameworkCore;
using NewHeap.Media.FileStructureStorage.SqlServer.Entities;
using NewHeap.Media.Models;
using NewHeap.Media.Modules;
using NewHeap.Platform.Common.Models;
using System.Linq.Expressions;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
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
            CreationDateTime = entity.CreationDateTime,
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
            await _dbContext.Folders.FirstOrDefaultAsync(x => x.Path == folderName && x.Name == folderName);
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
            CreationDateTime = entity.CreationDateTime,
        };
    }

    private TaskResult ValidateLengths(FileModel model)
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

    public async Task<FolderReference> CreateFolderAsync(string? path, string folderName)
    {
        path = NormalizePath(path);

        folderName = folderName.Replace(NhMediaValues.DirectorySeparator, string.Empty);

        var exists = await _dbContext.Folders.AnyAsync(x => x.Path == path && x.Name == folderName);
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
                    await _dbContext.Folders.FirstOrDefaultAsync(x => x.Path == currentPath && x.Name == part);
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

    public async Task<IEnumerable<FileReference>> GetFilesAsync(string? path, string? language,
        FileGetOptions? sortOptions)
    {
        var content = await GetFolderAsync(path, language, sortOptions);
        return content.Files;
    }

    public FolderReference GetFolderReference(FolderEntity folder)
    {
        return new FolderReference
        {
            Id = folder.Id,
            Path = folder.Path,
            Name = folder.Name,
            FullPath = MediaLibraryPath.Combine(folder.Path, folder.Name)
        };
    }

    public async Task<FolderReference> GetFolderReferenceAsync(string? path)
    {
        path = NormalizePath(path);
        MediaLibraryPath.Split(path, out var folderPath, out var folderName);
        var id = await _dbContext.Folders
            .Where(x => x.Path == path && x.Name == folderName)
            .Select(x => (Guid?)x.Id)
            .FirstOrDefaultAsync();

        return new FolderReference { Id = id, Path = folderPath, Name = folderName, FullPath = path };
    }

    public async Task<FolderContents> GetFolderAsync(string? path, string? language, FileGetOptions? sortOptions)
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

            var folderReference = GetFolderReference(folder);
            result.Folders.Add(folderReference);
        }

        var q = _dbContext.Files.AsNoTracking()
                .Where(x => x.Path == path)
            ;

        q = ProcessOrderBy(sortOptions, q);

        if (sortOptions?.PageSize != null)
        {
            q = q.Skip(sortOptions.Page * sortOptions.PageSize.Value)
                .Take(sortOptions.PageSize ?? 0);
        }

        var files = await q.ToArrayAsync();

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
                Folder = await GetFolderReferenceAsync(path),
                CreationDateTime = file.CreationDateTime,
            };
            result.Files.Add(fileReference);
            await ApplyLocalizations(fileReference, language);
        }

        return result;
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

    public async Task<FileReference?> GetFileAsync(string? path, string fileName, string? language)
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
            Folder = await GetFolderReferenceAsync(path),
            CreationDateTime = file.CreationDateTime,
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

    public async Task<TaskResult> DeleteFileAsync(string? path, string filename)
    {
        path = NormalizePath(path);
        var fileQuery = _dbContext.Files.Where(x => x.Path == path && x.Name == filename);
        await _dbContext.Localizations
            .Where(x => fileQuery.Select(y => y.Id).Contains(x.EntityId) && x.TypeName == nameof(FileEntity))
            .ExecuteDeleteAsync();
        var count = await fileQuery.ExecuteDeleteAsync();
        return count > 0 ? TaskResult.Succeeded() : TaskResult.Failed("Localization not found");
    }

    public async Task<SearchResults> SearchAsync(string searchTerm, string? path, SearchOptions options)
    {
        options.PageSize = Math.Max(options.PageSize, 10);
        options.PageIndex = Math.Max(options.PageIndex, 0);

        var results = new SearchResults() { PageIndex = options.PageIndex, ItemsPerPage = options.PageSize, };
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

        var total = await q.LongCountAsync();

        q = q.Skip(options.PageIndex * options.PageSize).Take(options.PageSize);

        results.TotalCount = total;
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
                Folder = await GetFolderReferenceAsync(file.Path),
                CreationDateTime = file.CreationDateTime
            };
            result.Add(reference);
            await ApplyLocalizations(reference, options.Language);
        }

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
        var file = await _dbContext.Files.FirstOrDefaultAsync(x => x.Path == path && x.Name == fileName);
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

        var folder = await _dbContext.Folders.FirstOrDefaultAsync(x => x.Path == path && x.Name == folderName);
        if (folder == null)
        {
            return null;
        }

        folder.Path = newPath;
        folder.Name = newName;
        await _dbContext.SaveChangesAsync();
        return await GetFolderReferenceAsync($"{newPath}/{newName}");
    }
}