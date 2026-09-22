using Microsoft.Extensions.Logging.Abstractions;
using NewHeap.Media;
using NewHeap.Media.EventHandlers;
using NewHeap.Media.Modules;
using NSubstitute;
using Xunit;

namespace NewHeap.Platform.Media.Tests;

public sealed class MediaLibraryFolderTests
{
    [Theory]
    [InlineData("documents", "documents")]
    [InlineData(" documents ", "documents")]
    [InlineData("\t\r\n\u00a0documents\u00a0\r\n\t", "documents")]
    [InlineData(" /documents/ ", "documents")]
    [InlineData(" project  documents ", "project  documents")]
    public async Task CreateFolderNormalizesTheNameBeforeStorageAndEvents(string input, string expected)
    {
        var storage = Substitute.For<IFileStructureStorage>();
        var handler = Substitute.For<IHandleMediaLibraryEvent>();
        var service = new MediaLibraryService([handler], new ThumbnailService(), storage,
            Substitute.For<IMediaStorage>(), new DefaultAuthorizationModule(),
            NullLogger<MediaLibraryService>.Instance);
        var folder = new FolderReference { Id = Guid.NewGuid(), Path = "/", Name = expected, FullPath = "/" + expected };
        storage.CreateFolderAsync("/", expected).Returns(folder);

        var result = await service.CreateFolderAsync("/", input);

        Assert.True(result.Success);
        Assert.Same(folder, result.Data);
        await storage.Received(1).CreateFolderAsync("/", expected);
        await handler.Received(1).HandleEvent(Arg.Is<MediaLibraryFolderEvent>(item =>
            item.Type == MediaLibraryFolderEventType.Adding && item.NewFolder!.Name == expected));
        await handler.Received(1).HandleEvent(Arg.Is<MediaLibraryFolderEvent>(item =>
            item.Type == MediaLibraryFolderEventType.Added && item.NewFolder == folder));
    }

    [Theory]
    [InlineData("documents", "documents")]
    [InlineData(" documents ", "documents")]
    [InlineData("\t\r\n\u00a0documents\u00a0\r\n\t", "documents")]
    [InlineData(" /documents/ ", "documents")]
    [InlineData(" project  documents ", "project  documents")]
    public async Task UpdateFolderNormalizesOnlyTheNewNameBeforeStorageAndEvents(string input, string expected)
    {
        var storage = Substitute.For<IFileStructureStorage>();
        var handler = Substitute.For<IHandleMediaLibraryEvent>();
        var service = new MediaLibraryService([handler], new ThumbnailService(), storage,
            Substitute.For<IMediaStorage>(), new DefaultAuthorizationModule(),
            NullLogger<MediaLibraryService>.Instance);
        var original = new FolderReference { Id = Guid.NewGuid(), Path = "/", Name = " old ", FullPath = "/ old " };
        var renamed = new FolderReference { Id = original.Id, Path = "/", Name = expected, FullPath = "/" + expected };
        storage.GetFolderReferenceAsync("/ old ").Returns(original);
        storage.MoveFolderAsync("/", " old ", "/", expected).Returns(renamed);

        var result = await service.UpdateFolderAsync("/", " old ", "/", input);

        Assert.True(result.Success);
        Assert.Same(renamed, result.Data);
        await storage.Received(1).GetFolderReferenceAsync("/ old ");
        await storage.Received(1).MoveFolderAsync("/", " old ", "/", expected);
        await handler.Received(2).HandleEvent(Arg.Is<MediaLibraryFolderEvent>(item =>
            item.NewFolder!.Name == expected));
        Assert.Equal(" old ", original.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n\u00a0")]
    [InlineData("/")]
    [InlineData(" / ")]
    [InlineData(" / / ")]
    public async Task EmptyNormalizedNamesFailWithoutStorageCallsOrEvents(string input)
    {
        var storage = Substitute.For<IFileStructureStorage>();
        var handler = Substitute.For<IHandleMediaLibraryEvent>();
        var service = new MediaLibraryService([handler], new ThumbnailService(), storage,
            Substitute.For<IMediaStorage>(), new DefaultAuthorizationModule(),
            NullLogger<MediaLibraryService>.Instance);

        var created = await service.CreateFolderAsync("/", input);
        var renamed = await service.UpdateFolderAsync("/", "documents", "/", input);

        Assert.False(created.Success);
        Assert.Null(created.Data);
        Assert.NotEmpty(created.GetResultItems());
        Assert.False(renamed.Success);
        Assert.Null(renamed.Data);
        Assert.NotEmpty(renamed.GetResultItems());
        Assert.Empty(storage.ReceivedCalls());
        Assert.Empty(handler.ReceivedCalls());
    }
}
