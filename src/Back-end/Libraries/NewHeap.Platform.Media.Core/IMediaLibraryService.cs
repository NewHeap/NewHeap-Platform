using NewHeap.Media.Models;
using NewHeap.Media.Modules;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Media;

public interface IMediaLibraryService
{
    /// <summary>
    /// Move / rename a file
    /// </summary>
    /// <param name="path">Current file path of the file</param>
    /// <param name="filename">Current filename of the file</param>
    /// <param name="newPath">New file path of the file</param>
    /// <param name="newFilename">New filename of the file</param>
    /// <returns></returns>
    Task<TaskResult> RenameFileAsync(string path, string filename, string newPath, string newFilename);
    
    /// <summary>
    /// Create a new file
    /// </summary>
    /// <param name="model"></param>
    /// <param name="file"></param>
    /// <returns></returns>
    Task<TaskResult<FileReference>> CreateFileAsync(FileModel model, Stream file);
    
    /// <summary>
    /// Create a new folder
    /// </summary>
    /// <param name="path"></param>
    /// <param name="folderName"></param>
    /// <returns></returns>
    Task<TaskResult<FolderReference>> CreateFolderAsync(string? path, string folderName);

    /// <summary>
    /// Update existing folder
    /// </summary>
    /// <param name="path"></param>
    /// <param name="folderName"></param>
    /// <param name="newPath"></param>
    /// <param name="newName"></param>
    /// <returns></returns>
    Task<TaskResult<FolderReference>> UpdateFolderAsync(string? path, string folderName, string? newPath,
        string newName);

    /// <summary>
    /// Get a file reference by path and filename
    /// </summary>
    /// <param name="path"></param>
    /// <param name="filename"></param>
    /// <param name="language"></param>
    /// <returns></returns>
    Task<TaskResult<FileReference>> GetFileAsync(string? path, string filename, string? language = null);
    
    /// <summary>
    /// Get a file reference by id
    /// </summary>
    /// <param name="id"></param>
    /// <returns></returns>
    Task<TaskResult<FileReference>> GetFileAsync(Guid id);

    /// <summary>
    /// Get file references for a set of ids, for example every image referenced by one page.
    /// Prefer this over calling <see cref="GetFileAsync(Guid)"/> in a loop: relational storage resolves
    /// the whole set with a fixed number of queries instead of two queries per file.
    /// </summary>
    /// <param name="ids">The file ids; duplicates are ignored.</param>
    /// <returns>The found file references keyed by id. Ids without a file are omitted.</returns>
    /// <remarks>
    /// Like <see cref="GetFileAsync(Guid)"/>, this id lookup does not call the path-based
    /// <see cref="IAuthorizationModule"/>; only resolve ids that content the caller may already see refers to.
    /// The default implementation calls <see cref="GetFileAsync(Guid)"/> once per distinct id.
    /// </remarks>
    async Task<IReadOnlyDictionary<Guid, FileReference>> GetFilesAsync(IEnumerable<Guid> ids)
    {
        var references = new Dictionary<Guid, FileReference>();
        foreach (var id in ids.Distinct())
        {
            var result = await GetFileAsync(id);
            if (result.Success && result.Data != null)
            {
                references.Add(id, result.Data);
            }
        }

        return references;
    }

    /// <summary>
    /// Get a stream of a file by path and filename
    /// </summary>
    /// <param name="path"></param>
    /// <param name="fileName"></param>
    /// <returns></returns>
    Task<DisposableTaskResult<Stream>> DownloadFileAsync(string? path, string fileName);
    
    /// <summary>
    /// Get a stream of a file by reference id
    /// </summary>
    /// <param name="id"></param>
    /// <returns></returns>
    Task<DisposableTaskResult<Stream>> DownloadFileAsync(Guid id);
    
    /// <summary>
    /// Get folder contents by path
    /// </summary>
    /// <param name="path"></param>
    /// <param name="language"></param>
    /// <param name="sortOptions"></param>
    /// <returns></returns>
    Task<FolderContents> GetFolder(string? path, string? language = null, FileGetOptions? sortOptions = null);
    
    /// <summary>
    /// Change file contents
    /// </summary>
    /// <param name="path"></param>
    /// <param name="fileName"></param>
    /// <param name="file"></param>
    /// <returns></returns>
    Task<TaskResult> UpdateFileAsync(string? path, string fileName, Stream file);
    
    /// <summary>
    /// Change file reference information
    /// </summary>
    /// <param name="id"></param>
    /// <param name="model"></param>
    /// <returns></returns>
    Task<TaskResult> UpdateFileAsync(Guid id, FileModel model);
    
    /// <summary>
    /// Delete a folder
    /// </summary>
    /// <param name="path"></param>
    /// <param name="folderName"></param>
    /// <returns></returns>
    Task<TaskResult> DeleteFolderAsync(string? path, string folderName);
    
    /// <summary>
    /// Delete a file
    /// </summary>
    /// <param name="path"></param>
    /// <param name="fileName"></param>
    /// <returns></returns>
    Task<TaskResult> DeleteFileAsync(string? path, string fileName);

    
    /// <summary>
    /// Search for files and folders
    /// </summary>
    /// <param name="path"></param>
    /// <param name="searchTerm"></param>
    /// <param name="options"></param>
    /// <returns></returns>
    Task<SearchResults> SearchAsync(string? path, string searchTerm, SearchOptions options);

    /// <summary>
    /// Localize a specific field of a file reference
    /// </summary>
    /// <param name="fileReferenceId"></param>
    /// <param name="propertyName"></param>
    /// <param name="language"></param>
    /// <param name="value"></param>
    /// <returns></returns>
    Task<TaskResult> LocalizeFieldAsync(Guid fileReferenceId, string propertyName, string language, string value);

    /// <summary>
    /// Change the tags of a file
    /// </summary>
    /// <param name="path"></param>
    /// <param name="fileName"></param>
    /// <param name="tags"></param>
    /// <returns></returns>
    Task<TaskResult> UpdateFileTagsAsync(string? path, string fileName, IEnumerable<string> tags);
    
    /// <summary>
    /// Move a file to a different folder, should not include filename
    /// </summary>
    /// <param name="id"></param>
    /// <param name="newPath"></param>
    /// <returns></returns>
    Task<TaskResult<FileReference>> MoveFileAsync(Guid id, string newPath);
    
    /// <summary>
    /// Change the filename of a file
    /// </summary>
    /// <param name="id"></param>
    /// <param name="newFilename"></param>
    /// <returns></returns>
    Task<TaskResult> RenameFileAsync(Guid id, string newFilename);
}