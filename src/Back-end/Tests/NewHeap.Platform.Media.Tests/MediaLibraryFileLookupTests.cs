using Microsoft.Extensions.Logging.Abstractions;
using NewHeap.Media;
using NewHeap.Media.Models;
using NewHeap.Media.Modules;
using NewHeap.Platform.Common.Models;
using NSubstitute;
using Xunit;

namespace NewHeap.Platform.Media.Tests;

public sealed class MediaLibraryFileLookupTests
{
    [Fact]
    public async Task GetFilesAsyncResolvesTheSetWithOneStorageCallAndAddsThumbnails()
    {
        var storage = Substitute.For<IFileStructureStorage>();
        var thumbnails = Substitute.For<IThumbnailService>();
        var service = new MediaLibraryService([], thumbnails, storage, Substitute.For<IMediaStorage>(),
            new DefaultAuthorizationModule(), NullLogger<MediaLibraryService>.Instance);
        var first = CreateFile("first.png");
        var second = CreateFile("second.png");
        Guid[] ids = [first.Id, second.Id, Guid.NewGuid()];
        storage.GetByIdsAsync(ids).Returns(new Dictionary<Guid, FileReference>
        {
            [first.Id] = first,
            [second.Id] = second
        });
        thumbnails.GetThumbnailAsync(first.Id).Returns("first-thumbnail");
        thumbnails.GetThumbnailAsync(second.Id).Returns((string?)null);

        var files = await service.GetFilesAsync(ids);

        Assert.Equal(2, files.Count);
        Assert.Equal("first-thumbnail", files[first.Id].Thumbnail);
        Assert.Null(files[second.Id].Thumbnail);
        await storage.Received(1).GetByIdsAsync(ids);
        await storage.DidNotReceive().GetByIdAsync(Arg.Any<Guid>());
    }

    [Fact]
    public async Task CustomStorageWithoutBatchLookupFallsBackToOneLookupPerDistinctId()
    {
        var file = CreateFile("first.png");
        IFileStructureStorage storage = new ByIdOnlyFileStructureStorage(file);
        var missingId = Guid.NewGuid();

        var files = await storage.GetByIdsAsync([file.Id, file.Id, missingId]);

        Assert.Same(file, Assert.Single(files).Value);
        Assert.Equal([file.Id, missingId], ((ByIdOnlyFileStructureStorage)storage).RequestedIds);
    }

    [Fact]
    public async Task CustomMediaLibraryWithoutBatchLookupFallsBackToOneLookupPerDistinctId()
    {
        var file = CreateFile("first.png");
        IMediaLibraryService mediaLibrary = new ByIdOnlyMediaLibraryService(file);
        var missingId = Guid.NewGuid();

        var files = await mediaLibrary.GetFilesAsync([file.Id, file.Id, missingId]);

        Assert.Same(file, Assert.Single(files).Value);
        Assert.Equal([file.Id, missingId], ((ByIdOnlyMediaLibraryService)mediaLibrary).RequestedIds);
    }

    private static FileReference CreateFile(string name)
    {
        return new FileReference
        {
            Id = Guid.NewGuid(),
            Name = name,
            Folder = new FolderReference { Id = Guid.NewGuid(), Path = "/", Name = "images", FullPath = "/images" }
        };
    }

    /// <summary>
    /// An implementation written before <see cref="IFileStructureStorage.GetByIdsAsync"/> existed.
    /// </summary>
    private sealed class ByIdOnlyFileStructureStorage(FileReference file) : IFileStructureStorage
    {
        public List<Guid> RequestedIds { get; } = [];

        public Task<FileReference?> GetByIdAsync(Guid id)
        {
            RequestedIds.Add(id);
            return Task.FromResult(id == file.Id ? file : null);
        }

        public Task<TaskResult<FileReference>> CreateFileAsync(FileModel model, Guid id) =>
            throw new NotSupportedException();

        public Task<TaskResult<FileReference>> UpdateFileAsync(Guid id, FileModel model) =>
            throw new NotSupportedException();

        public Task<FolderReference> CreateFolderAsync(string? path, string folderName) =>
            throw new NotSupportedException();

        public Task<bool> DeleteFolderAsync(string? path, string folderName) =>
            throw new NotSupportedException();

        public Task<IEnumerable<FileReference>> GetFilesAsync(string? path, string? language,
            FileGetOptions? sortOptions) =>
            throw new NotSupportedException();

        public Task<FolderContents> GetFolderAsync(string? path, string? language, FileGetOptions? sortOptions) =>
            throw new NotSupportedException();

        public Task<FileReference?> GetFileAsync(string? path, string fileName, string? language) =>
            throw new NotSupportedException();

        public Task<TaskResult> DeleteFileAsync(string? path, string filename) =>
            throw new NotSupportedException();

        public Task<SearchResults> SearchAsync(string searchTerm, string? path, SearchOptions options) =>
            throw new NotSupportedException();

        public Task<TaskResult> LocalizeAsync(Guid entityId, string language, string propertyName, string value) =>
            throw new NotSupportedException();

        public Task<TaskResult> UpdateTagsAsync(string path, string fileName, IEnumerable<string> tags) =>
            throw new NotSupportedException();

        public Task<FolderReference?> MoveFolderAsync(string? path, string folderName, string newPath,
            string newName) =>
            throw new NotSupportedException();

        public Task<FolderReference> GetFolderReferenceAsync(string? path) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// An implementation written before <see cref="IMediaLibraryService.GetFilesAsync"/> existed.
    /// </summary>
    private sealed class ByIdOnlyMediaLibraryService(FileReference file) : IMediaLibraryService
    {
        public List<Guid> RequestedIds { get; } = [];

        public Task<TaskResult<FileReference>> GetFileAsync(Guid id)
        {
            RequestedIds.Add(id);
            return Task.FromResult(id == file.Id
                ? TaskResult<FileReference>.Succeeded(file)
                : TaskResult<FileReference>.Failed("File not found"));
        }

        public Task<TaskResult> RenameFileAsync(string path, string filename, string newPath, string newFilename) =>
            throw new NotSupportedException();

        public Task<TaskResult<FileReference>> CreateFileAsync(FileModel model, Stream file) =>
            throw new NotSupportedException();

        public Task<TaskResult<FolderReference>> CreateFolderAsync(string? path, string folderName) =>
            throw new NotSupportedException();

        public Task<TaskResult<FolderReference>> UpdateFolderAsync(string? path, string folderName, string? newPath,
            string newName) =>
            throw new NotSupportedException();

        public Task<TaskResult<FileReference>> GetFileAsync(string? path, string filename, string? language = null) =>
            throw new NotSupportedException();

        public Task<DisposableTaskResult<Stream>> DownloadFileAsync(string? path, string fileName) =>
            throw new NotSupportedException();

        public Task<DisposableTaskResult<Stream>> DownloadFileAsync(Guid id) =>
            throw new NotSupportedException();

        public Task<FolderContents> GetFolder(string? path, string? language = null,
            FileGetOptions? sortOptions = null) =>
            throw new NotSupportedException();

        public Task<TaskResult> UpdateFileAsync(string? path, string fileName, Stream file) =>
            throw new NotSupportedException();

        public Task<TaskResult> UpdateFileAsync(Guid id, FileModel model) =>
            throw new NotSupportedException();

        public Task<TaskResult> DeleteFolderAsync(string? path, string folderName) =>
            throw new NotSupportedException();

        public Task<TaskResult> DeleteFileAsync(string? path, string fileName) =>
            throw new NotSupportedException();

        public Task<SearchResults> SearchAsync(string? path, string searchTerm, SearchOptions options) =>
            throw new NotSupportedException();

        public Task<TaskResult> LocalizeFieldAsync(Guid fileReferenceId, string propertyName, string language,
            string value) =>
            throw new NotSupportedException();

        public Task<TaskResult> UpdateFileTagsAsync(string? path, string fileName, IEnumerable<string> tags) =>
            throw new NotSupportedException();

        public Task<TaskResult<FileReference>> MoveFileAsync(Guid id, string newPath) =>
            throw new NotSupportedException();

        public Task<TaskResult> RenameFileAsync(Guid id, string newFilename) =>
            throw new NotSupportedException();
    }
}
