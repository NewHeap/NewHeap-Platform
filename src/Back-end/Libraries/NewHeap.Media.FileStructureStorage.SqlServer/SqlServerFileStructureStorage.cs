using Microsoft.EntityFrameworkCore;
using NewHeap.Media.FileStructureStorage.SqlServer.Entities;
using NewHeap.Media.Modules;

namespace NewHeap.Media.FileStructureStorage.SqlServer;

internal class SqlServerFileStructureStorage : IFileStructureStorage
{
    private readonly FileStructureDbContext _dbContext;

    private static Dictionary<string, Action<FileReference,object>>? _fileReferenceAccessors = null;
    
    private static Dictionary<string, Action<FileReference,object>> FileReferenceAccessors
    {
        get
        {
            if (_fileReferenceAccessors != null)
            {
                return _fileReferenceAccessors;
            }
            
            _fileReferenceAccessors = new Dictionary<string, Action<FileReference,object>>();
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
    
    public async Task<FileReference> CreateFile(string? path, string fileName, Guid id)
    {
        var file = new FileEntity
        {
            Id = id,
            Path = path,
            Name = fileName
        };


        if (path != null)
        {
            string? folderPath = null;
            var folderName = path;

            var sep = path.LastIndexOf(NhMediaValues.DirectorySeparator, StringComparison.Ordinal);
            if (sep != -1)
            {
                folderPath = path[..sep];
                folderName = path[(sep + 1)..];
            }
            var folderExists = await _dbContext.Folders.AnyAsync(x => x.Path == folderPath && x.Name == folderName);
            if (!folderExists)
            {
                await CreateFolder(folderPath, folderName);
            }
        }
        
        _dbContext.Files.Add(file);
        await _dbContext.SaveChangesAsync();

        return new FileReference
        {
            Id = file.Id,
            Name = file.Name,
            Folder = await GetFolderReference(path)
        };
    }

    public async Task<FolderReference> CreateFolder(string? path, string folderName)
    {
        if (path != null)
        {
            var parts = path.Split(NhMediaValues.DirectorySeparator);
            string? currentPath = null;
            foreach (var part in parts)
            {
                var existing = await _dbContext.Folders.FirstOrDefaultAsync(x => x.Path == currentPath && x.Name == part);
                if (existing == null)
                {
                    existing = new FolderEntity
                    {
                        Name = part,
                        Path = currentPath
                    };
                    _dbContext.Folders.Add(existing);
                }

                if (currentPath == null)
                {
                    currentPath = part;
                }
                else
                {
                    currentPath += NhMediaValues.DirectorySeparator + part;
                }
            }
        }
        
        var folder = new FolderEntity
        {
            Name = folderName,
            Path = path
        };
        
        _dbContext.Folders.Add(folder);
        await _dbContext.SaveChangesAsync();
        
        return new FolderReference
        {
            Path = folder.Path,
            Name = folder.Name
        };
    }

    public async Task<bool> DeleteFolder(string? path, string folderName)
    {
        var fullPath = path != null ? path + NhMediaValues.DirectorySeparator + folderName : folderName;

        var fileIds = await _dbContext.Files.Where(x => x.Path!.StartsWith(fullPath)).Select(x => x.Id)
            .ToListAsync();
        
        await _dbContext.Localizations.Where(x => fileIds.Contains(x.EntityId) && x.TypeName == nameof(FileEntity)).ExecuteDeleteAsync();
        await _dbContext.Files.Where(x => x.Path!.StartsWith(fullPath)).ExecuteDeleteAsync();
        await _dbContext.Folders.Where(x => x.Path!.StartsWith(fullPath)).ExecuteDeleteAsync();
        await _dbContext.Folders.Where(x => x.Path == path && x.Name == folderName).ExecuteDeleteAsync();
        return true;
    }

    public async Task<IEnumerable<FileReference>> GetFiles(string? path, string? language)
    {
        var content = await GetFolder(path, language);
        return content.Files;
    }

    private Task<FolderReference> GetFolderReference(string? path)
    {
        path ??= "";
        var splitAt = path.LastIndexOf('/');
        if (splitAt == -1)
        {
            return Task.FromResult(new FolderReference
            {
                Path = null,
                Name = ""
            });
        }
        var folderPath  = path[0..splitAt];
        var folderName = path[(splitAt + 1)..];
        
        return Task.FromResult(new FolderReference
        {
            Path = folderPath,
            Name = folderName
        });
    }
    
    public async Task<FolderContents> GetFolder(string? path, string? language)
    {
        var result = new FolderContents();
        var folders = await _dbContext.Folders.AsNoTracking()
            .Where(x => x.Path == path)
            .ToArrayAsync();

        foreach (var folder in folders)
        {
            var folderReference = new FolderReference
            {
                Path = folder.Path,
                Name = folder.Name
            };
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
                Folder = await GetFolderReference(file.Path)
            };
            result.Files.Add(fileReference);
            await ApplyLocalizations(fileReference, language);
        }

        return result;
    }

    public async Task<FileReference?> GetFile(string? path, string fileName, string? language)
    {
        var file = await _dbContext.Files.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Path == path && x.Name == fileName);
        if(file == null)
        {
            return null;
        }
        
        var reference = new FileReference
        {
            Id = file.Id,
            Name = file.Name,
            Folder = await GetFolderReference(path)
        };

        await ApplyLocalizations(reference, language);
        return reference;
    }

    private async Task ApplyLocalizations(FileReference file, string? language)
    {
        if(string.IsNullOrEmpty(language))
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
        var fileQuery = _dbContext.Files.Where(x => x.Path == path && x.Name == filename);
        await _dbContext.Localizations.Where(x => fileQuery.Select(y => y.Id).Contains(x.EntityId) && x.TypeName == nameof(FileEntity)).ExecuteDeleteAsync();
        var count = await fileQuery.ExecuteDeleteAsync();
        return count > 0;
    }

    public async Task<IEnumerable<FileReference>> Search(string searchTerm, string? path, string? language)
    {
        var q = _dbContext.Files.AsNoTracking();
        if (!string.IsNullOrEmpty(path))
        {
            q = q.Where(x => x.Path!.StartsWith(path));
        }
        q = q.Where(x => x.Name.Contains(searchTerm));
        var files = await q.ToListAsync();
        var result = new List<FileReference>();
        foreach (var file in files)
        {
            var reference = new FileReference
            {
                Id = file.Id,
                Name = file.Name,
                Folder = await GetFolderReference(file.Path)
            };
            result.Add(reference);
            await ApplyLocalizations(reference, language);
        }
        return result;
    }

    public async Task Localize(Guid entityId, string language, string propertyName, string? value)
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
                return; // Doesn't exist, nothing to delete
            }
            
            _dbContext.Localizations.Remove(entity);
            await _dbContext.SaveChangesAsync();
            return;
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
    }
}