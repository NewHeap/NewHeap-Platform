using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NewHeap.Media.Modules;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.Media.MediaStorage.FileSystem;

public class DefaultMediaStorage : IMediaStorage
{
    private readonly IOptionsSnapshot<DefaultMediaStorageSettings> _settings;
    private readonly ILogger<DefaultMediaStorage> _logger;

    public DefaultMediaStorage(
        IOptionsSnapshot<DefaultMediaStorageSettings> settings,
        ILogger<DefaultMediaStorage> logger)
    {
        _settings = settings;
        _logger = logger;
    }
    
    public async Task<Guid> SaveFileAsync(Stream file)
    {
        var fileId = Guid.NewGuid();
        _logger.LogDebug("Saving file {fileId}", fileId);
        var dir = GetDir(fileId);
        var root = _settings.Value.StoragePath;
        Directory.CreateDirectory(Path.Combine(root, dir));
        var filePath = Path.Combine(root, dir, fileId.ToString());
        await using var fileStream = File.Create(filePath);
        await file.CopyToAsync(fileStream);
        _logger.LogDebug("File saved {fileId} to {path}", fileId,filePath);
        return fileId;
    }

    public async Task<TaskResult> UpdateFileAsync(Stream fileStream, Guid id)
    {
        var dir = GetDir(id);
        var root = _settings.Value.StoragePath;

        var filePath = Path.Combine(root, dir, id.ToString());
        if (!File.Exists(filePath))
        {
            return TaskResult.Failed("File not found");
        }

        await using var file = File.Open(filePath, FileMode.Create);
        await fileStream.CopyToAsync(file);
        _logger.LogDebug("File updated {fileId} at {path}", id,filePath);

        return TaskResult.Succeeded();
    }

    public Task<TaskResult> DeleteAsync(Guid id)
    {
        var dir = GetDir(id);
        var root = _settings.Value.StoragePath;

        var filePath = Path.Combine(root, dir, id.ToString());
        if (!File.Exists(filePath))
        {
            return Task.FromResult(TaskResult.Failed("File not found"));
        }
        
        File.Delete(filePath);
        _logger.LogDebug("File deleted {fileId} at {path}", id,filePath);
        return Task.FromResult(TaskResult.Succeeded());
    }

    public Task<Stream?> GetFileAsync(Guid fileRefId)
    {
        var dir = GetDir(fileRefId);
        var root = _settings.Value.StoragePath;
        var filePath = Path.Combine(root, dir, fileRefId.ToString());
        if (!File.Exists(filePath))
        {
            return Task.FromResult<Stream?>(null);
        }
        
        return Task.FromResult<Stream?>(File.OpenRead(filePath));
    }

    private string GetDir(Guid fileId)
    {
        return fileId.ToString("N")[0..4];
    }
}


public class DefaultMediaStorageSettings
{
    public string StoragePath { get; set; } = null!;
}