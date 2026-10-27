using Microsoft.Extensions.Logging.Abstractions;
using NewHeap.Media;
using NewHeap.Media.EventHandlers;
using NewHeap.Media.Modules;
using NewHeap.Platform.Common.Models;
using NSubstitute;
using Xunit;

namespace NewHeap.Platform.Media.Tests;

/// <summary>
/// Mutations of <see cref="MediaLibraryService"/> ask the <see cref="IAuthorizationModule"/> about every place
/// they write to before they read or write storage, under a module that, like a tenant-scoped consumer
/// module, only allows paths below one division.
/// </summary>
public sealed class MediaLibraryMutationAuthorizationTests
{
    private const string OwnDocuments = "/divisions/a/projects/docs";
    private const string OtherDocuments = "/divisions/b/projects/docs";

    [Fact]
    public async Task MovingAFileAuthorizesTheDestinationAsACreateBeforeStorageIsTouched()
    {
        var context = new ServiceContext();
        var file = context.AddFile(OwnDocuments, "report.pdf");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            context.Service.UpdateFileAsync(file.Id, new FileModel { Path = OtherDocuments, Name = "report.pdf" }));

        Assert.Equal(
            [
                new Decision(ActionType.Update, OwnDocuments, "report.pdf"),
                new Decision(ActionType.Create, OtherDocuments, "report.pdf")
            ],
            context.Module.Decisions);
        await context.Storage.DidNotReceive().GetFileAsync(Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string?>());
        await context.Storage.DidNotReceive().UpdateFileAsync(Arg.Any<Guid>(), Arg.Any<FileModel>());
        Assert.Empty(context.Handler.ReceivedCalls());
    }

    [Fact]
    public async Task RenamingAFileAuthorizesTheNewNameAsACreateInItsFolder()
    {
        var context = new ServiceContext();
        var file = context.AddFile(OwnDocuments, "report.pdf");

        var result = await context.Service.UpdateFileAsync(file.Id,
            new FileModel { Path = OwnDocuments, Name = "summary.pdf" });

        Assert.True(result.Success);
        Assert.Equal(
            [
                new Decision(ActionType.Update, OwnDocuments, "report.pdf"),
                new Decision(ActionType.Create, OwnDocuments, "summary.pdf")
            ],
            context.Module.Decisions);
        await context.Storage.Received(1).UpdateFileAsync(file.Id,
            Arg.Is<FileModel>(model => model.Path == OwnDocuments && model.Name == "summary.pdf"));
    }

    [Fact]
    public async Task UpdatingMetadataInPlaceNeedsNoDestinationCheck()
    {
        var context = new ServiceContext();
        var file = context.AddFile(OwnDocuments, "report.pdf");

        var result = await context.Service.UpdateFileAsync(file.Id,
            new FileModel { Path = OwnDocuments, Name = "report.pdf", Title = "Quarterly report" });

        Assert.True(result.Success);
        Assert.Equal([new Decision(ActionType.Update, OwnDocuments, "report.pdf")], context.Module.Decisions);
        await context.Storage.DidNotReceive().GetFileAsync(Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task AFileMoveOntoAnExistingNameFailsWithoutWritingOrEvents()
    {
        var context = new ServiceContext();
        var file = context.AddFile(OwnDocuments + "/drafts", "report.pdf");
        context.AddFile(OwnDocuments, "report.pdf");

        var result = await context.Service.UpdateFileAsync(file.Id,
            new FileModel { Path = OwnDocuments, Name = "report.pdf" });

        Assert.False(result.Success);
        AssertError(result, nameof(FileModel.Name), NhMediaErrorCodes.FileNameExists);
        await context.Storage.DidNotReceive().UpdateFileAsync(Arg.Any<Guid>(), Arg.Any<FileModel>());
        Assert.Empty(context.Handler.ReceivedCalls());
    }

    [Fact]
    public async Task AFileWhoseOwnEntryAnswersTheDestinationLookupIsNotAConflict()
    {
        // A provider with case-insensitive lookups finds the renamed file itself for a change of case.
        var context = new ServiceContext();
        var file = context.AddFile(OwnDocuments, "report.pdf");
        context.Storage.GetFileAsync(OwnDocuments, "Report.pdf", null).Returns(file);

        var result = await context.Service.UpdateFileAsync(file.Id,
            new FileModel { Path = OwnDocuments, Name = "Report.pdf" });

        Assert.True(result.Success);
        await context.Storage.Received(1).UpdateFileAsync(file.Id, Arg.Any<FileModel>());
    }

    [Fact]
    public async Task MoveFileAsyncAuthorizesTheFolderItPlacesTheFileIn()
    {
        var context = new ServiceContext();
        var file = context.AddFile(OwnDocuments, "report.pdf");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            context.Service.MoveFileAsync(file.Id, OtherDocuments + "/report.pdf"));

        Assert.Equal(new Decision(ActionType.Create, OtherDocuments, "report.pdf"), context.Module.Decisions[^1]);
        await context.Storage.DidNotReceive().UpdateFileAsync(Arg.Any<Guid>(), Arg.Any<FileModel>());
        Assert.Empty(context.Handler.ReceivedCalls());
    }

    [Fact]
    public async Task MoveFileAsyncRefusesAnExistingNameInTheDestination()
    {
        var context = new ServiceContext();
        var file = context.AddFile(OwnDocuments + "/drafts", "report.pdf");
        context.AddFile(OwnDocuments, "report.pdf");

        var result = await context.Service.MoveFileAsync(file.Id, OwnDocuments + "/report.pdf");

        Assert.False(result.Success);
        AssertError(result, nameof(FileModel.Name), NhMediaErrorCodes.FileNameExists);
        await context.Storage.DidNotReceive().UpdateFileAsync(Arg.Any<Guid>(), Arg.Any<FileModel>());
    }

    [Fact]
    public async Task RenameFileAsyncByIdAuthorizesTheNewName()
    {
        var context = new ServiceContext(allowedRoot: OwnDocuments + "/public");
        var file = context.AddFile(OwnDocuments + "/public", "report.pdf");
        context.Module.Deny(decision => decision.Action == ActionType.Create);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            context.Service.RenameFileAsync(file.Id, "summary.pdf"));

        Assert.Equal(
            new Decision(ActionType.Create, OwnDocuments + "/public", "summary.pdf"),
            context.Module.Decisions[^1]);
        await context.Storage.DidNotReceive().UpdateFileAsync(Arg.Any<Guid>(), Arg.Any<FileModel>());
    }

    [Fact]
    public async Task RenameFileAsyncByPathAuthorizesTheNewPathAndName()
    {
        var context = new ServiceContext();
        var file = context.AddFile(OwnDocuments, "report.pdf");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            context.Service.RenameFileAsync(OwnDocuments, "report.pdf", OtherDocuments, "report.pdf"));

        Assert.Equal(
            [
                new Decision(ActionType.Update, OwnDocuments, "report.pdf"),
                new Decision(ActionType.Create, OtherDocuments, "report.pdf")
            ],
            context.Module.Decisions);
        await context.Storage.DidNotReceive().UpdateFileAsync(file.Id, Arg.Any<FileModel>());
    }

    [Fact]
    public async Task MovingAFolderAuthorizesTheDestinationParentAsACreateBeforeStorageIsTouched()
    {
        var context = new ServiceContext();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            context.Service.UpdateFolderAsync("/divisions/a/projects", "staging", "/divisions/b/projects", "planted"));

        Assert.Equal(
            [
                new Decision(ActionType.Update, "/divisions/a/projects/staging", null),
                new Decision(ActionType.Create, "/divisions/b/projects", null)
            ],
            context.Module.Decisions);
        Assert.Empty(context.Storage.ReceivedCalls());
        Assert.Empty(context.Handler.ReceivedCalls());
    }

    [Fact]
    public async Task RenamingAFolderInPlaceAuthorizesTheNewNameInItsParent()
    {
        var context = new ServiceContext();
        var folder = context.AddFolder("/divisions/a/projects", "staging");
        context.Storage.MoveFolderAsync("/divisions/a/projects", "staging", "/divisions/a/projects", "archive")
            .Returns(new FolderReference
            {
                Id = folder.Id, Path = "/divisions/a/projects", Name = "archive", FullPath = "/divisions/a/projects/archive"
            });

        var result = await context.Service.UpdateFolderAsync("/divisions/a/projects", "staging",
            "/divisions/a/projects", "archive");

        Assert.True(result.Success);
        Assert.Equal(new Decision(ActionType.Create, "/divisions/a/projects", null), context.Module.Decisions[^1]);
        await context.Storage.Received(1).GetFolderReferenceAsync("/divisions/a/projects/archive");
    }

    [Fact]
    public async Task AFolderMoveOntoAnExistingFolderFailsWithoutMovingOrEvents()
    {
        var context = new ServiceContext();
        context.AddFolder("/divisions/a/projects", "staging");
        context.AddFolder("/divisions/a/projects", "archive");

        var result = await context.Service.UpdateFolderAsync("/divisions/a/projects", "staging",
            "/divisions/a/projects", "archive");

        Assert.False(result.Success);
        AssertError(result, "newName", NhMediaErrorCodes.FolderNameExists);
        await context.Storage.DidNotReceive().MoveFolderAsync(Arg.Any<string?>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>());
        Assert.Empty(context.Handler.ReceivedCalls());
    }

    [Fact]
    public async Task AFolderWhoseOwnEntryAnswersTheDestinationLookupIsNotAConflict()
    {
        var context = new ServiceContext();
        var folder = context.AddFolder("/divisions/a/projects", "staging");
        context.Storage.GetFolderReferenceAsync("/divisions/a/projects/Staging").Returns(folder);
        context.Storage.MoveFolderAsync("/divisions/a/projects", "staging", "/divisions/a/projects", "Staging")
            .Returns(folder);

        var result = await context.Service.UpdateFolderAsync("/divisions/a/projects", "staging",
            "/divisions/a/projects", "Staging");

        Assert.True(result.Success);
        await context.Storage.Received(1).MoveFolderAsync("/divisions/a/projects", "staging",
            "/divisions/a/projects", "Staging");
    }

    [Theory]
    [InlineData("secret.pdf")]
    [InlineData("does-not-exist.pdf")]
    public async Task UpdatingTagsAuthorizesAnUpdateOfTheFileBeforeItIsRead(string fileName)
    {
        var context = new ServiceContext();
        context.AddFile(OtherDocuments, "secret.pdf");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            context.Service.UpdateFileTagsAsync(OtherDocuments, fileName, ["planted"]));

        // An existing and a missing file are refused alike, before any lookup.
        Assert.Equal([new Decision(ActionType.Update, OtherDocuments, fileName)], context.Module.Decisions);
        await context.Storage.DidNotReceive().GetFileAsync(Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string?>());
        await context.Storage.DidNotReceive().UpdateTagsAsync(Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IEnumerable<string>>());
        Assert.Empty(context.Handler.ReceivedCalls());
    }

    [Fact]
    public async Task UpdatingTagsOfAnAuthorizedFileWritesThem()
    {
        var context = new ServiceContext();
        context.AddFile(OwnDocuments, "report.pdf");
        context.Storage.UpdateTagsAsync(OwnDocuments, "report.pdf", Arg.Any<IEnumerable<string>>())
            .Returns(TaskResult.Succeeded());

        var result = await context.Service.UpdateFileTagsAsync(OwnDocuments, "report.pdf", ["quarterly"]);

        Assert.True(result.Success);
        Assert.Equal([new Decision(ActionType.Update, OwnDocuments, "report.pdf")], context.Module.Decisions);
        await context.Storage.Received(1).UpdateTagsAsync(OwnDocuments, "report.pdf",
            Arg.Is<IEnumerable<string>>(tags => tags.SequenceEqual(new[] { "quarterly" })));
    }

    [Fact]
    public async Task LocalizingAFieldAuthorizesAnUpdateOfTheFile()
    {
        var context = new ServiceContext();
        var file = context.AddFile(OwnDocuments, "plan.txt");
        context.Module.Deny(decision => decision.Action != ActionType.Read);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            context.Service.LocalizeFieldAsync(file.Id, "Title", "nl", "Defaced by a viewer"));

        Assert.Equal([new Decision(ActionType.Update, OwnDocuments, "plan.txt", "nl")], context.Module.Decisions);
        await context.Storage.DidNotReceive().LocalizeAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>());
    }

    [Theory]
    [InlineData("Name")]
    [InlineData("Id")]
    [InlineData("Creator")]
    [InlineData("Folder")]
    [InlineData("Tags")]
    [InlineData("MetaData")]
    [InlineData("CreationDateTime")]
    public async Task OnlyDisplayStringsCanBeLocalized(string propertyName)
    {
        var context = new ServiceContext();
        var file = context.AddFile(OwnDocuments, "plan.txt");

        var result = await context.Service.LocalizeFieldAsync(file.Id, propertyName, "nl", "approved-invoice.pdf");

        Assert.False(result.Success);
        AssertError(result, "propertyName", NhMediaErrorCodes.PropertyNotLocalizable);
        await context.Storage.DidNotReceive().LocalizeAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>());
    }

    [Theory]
    [InlineData("Title")]
    [InlineData("description")]
    [InlineData("altText")]
    public async Task DisplayStringsAreLocalizedThroughStorage(string propertyName)
    {
        var context = new ServiceContext();
        var file = context.AddFile(OwnDocuments, "plan.txt");
        context.Storage.LocalizeAsync(file.Id, "nl", propertyName, "Projectplan").Returns(TaskResult.Succeeded());

        var result = await context.Service.LocalizeFieldAsync(file.Id, propertyName, "nl", "Projectplan");

        Assert.True(result.Success);
        await context.Storage.Received(1).LocalizeAsync(file.Id, "nl", propertyName, "Projectplan");
    }

    [Fact]
    public async Task LocalizingAMissingFileFailsWithoutWriting()
    {
        var context = new ServiceContext();

        var result = await context.Service.LocalizeFieldAsync(Guid.NewGuid(), "Title", "nl", "Projectplan");

        Assert.False(result.Success);
        Assert.Empty(context.Module.Decisions);
        await context.Storage.DidNotReceive().LocalizeAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>());
    }

    private static void AssertError(TaskResult result, string name, string errorCode)
    {
        var item = Assert.Single(result.GetResultItems());
        Assert.Equal(name, item.Name);
        Assert.Equal(errorCode, Assert.Single(item.ErrorMessages).ToString());
    }

    public sealed record Decision(ActionType Action, string? Path, string? FileName, string? Language = null);

    /// <summary>
    /// Allows every path at or below one root, like a consumer module that scopes media to a division, and
    /// records each decision it was asked for.
    /// </summary>
    public sealed class ScopedAuthorizationModule(string allowedRoot) : IAuthorizationModule
    {
        private readonly List<Func<Decision, bool>> _denials = [];

        public List<Decision> Decisions { get; } = [];

        public void Deny(Func<Decision, bool> denial)
        {
            _denials.Add(denial);
        }

        public Task IsAuthorizedAsync(AuthorizationContext context)
        {
            var decision = new Decision(context.Action, context.Path, context.FileName, context.Language);
            Decisions.Add(decision);

            var path = context.Path ?? NhMediaValues.DirectorySeparator;
            var scoped = path == allowedRoot || path.StartsWith(allowedRoot + NhMediaValues.DirectorySeparator,
                StringComparison.Ordinal);
            context.Authorized = scoped && !_denials.Any(denial => denial(decision));
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// A <see cref="MediaLibraryService"/> over substituted storage that answers lookups for the files and
    /// folders a test adds.
    /// </summary>
    public sealed class ServiceContext
    {
        public ServiceContext(string allowedRoot = "/divisions/a")
        {
            Module = new ScopedAuthorizationModule(allowedRoot);
            Service = new MediaLibraryService([Handler], new ThumbnailService(), Storage,
                Blobs, Module, NullLogger<MediaLibraryService>.Instance);
            Storage.UpdateFileAsync(Arg.Any<Guid>(), Arg.Any<FileModel>())
                .Returns(call => TaskResult<FileReference>.Succeeded(new FileReference
                {
                    Id = call.Arg<Guid>(),
                    Name = call.Arg<FileModel>().Name!,
                    Folder = CreateFolder(call.Arg<FileModel>().Path ?? NhMediaValues.DirectorySeparator)
                }));
            Storage.GetFolderReferenceAsync(Arg.Any<string?>())
                .Returns(call => CreateFolder(call.Arg<string?>() ?? NhMediaValues.DirectorySeparator));
        }

        public IFileStructureStorage Storage { get; } = Substitute.For<IFileStructureStorage>();

        public IMediaStorage Blobs { get; } = Substitute.For<IMediaStorage>();

        public IHandleMediaLibraryEvent Handler { get; } = Substitute.For<IHandleMediaLibraryEvent>();

        public ScopedAuthorizationModule Module { get; }

        public MediaLibraryService Service { get; }

        public FileReference AddFile(string folderPath, string name)
        {
            var file = new FileReference { Id = Guid.NewGuid(), Name = name, Folder = CreateFolder(folderPath) };
            Storage.GetByIdAsync(file.Id).Returns(file);
            Storage.GetFileAsync(folderPath, name, null).Returns(file);
            return file;
        }

        /// <summary>
        /// Adds a file whose stored bytes are <paramref name="content"/>; every read gets a new stream.
        /// </summary>
        public FileReference AddFile(string folderPath, string name, byte[] content)
        {
            var file = AddFile(folderPath, name);
            Blobs.GetFileAsync(file.Id).Returns(_ => new MemoryStream(content));
            return file;
        }

        public FolderReference AddFolder(string parentPath, string name)
        {
            var folder = CreateFolder(MediaLibraryPath.Combine(parentPath, name));
            folder.Id = Guid.NewGuid();
            Storage.GetFolderReferenceAsync(folder.FullPath).Returns(folder);
            return folder;
        }

        private static FolderReference CreateFolder(string fullPath)
        {
            MediaLibraryPath.Split(fullPath, out var path, out var name);
            return new FolderReference { Path = path, Name = name, FullPath = fullPath };
        }
    }
}
