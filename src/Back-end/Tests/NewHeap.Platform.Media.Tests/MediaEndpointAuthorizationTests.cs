using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NewHeap.Media;
using NewHeap.Media.Modules;
using NewHeap.Platform.Common.Models;
using NSubstitute;
using Xunit;
using ServiceContext = NewHeap.Platform.Media.Tests.MediaLibraryMutationAuthorizationTests.ServiceContext;

namespace NewHeap.Platform.Media.Tests;

/// <summary>
/// The media mutation endpoints answer a refused authorization with 401, like the other media endpoints,
/// and a destination that already holds the name with 409.
/// </summary>
public sealed class MediaEndpointAuthorizationTests(MediaEndpointAuthorizationTests.Host host)
    : IClassFixture<MediaEndpointAuthorizationTests.Host>
{
    private const string OwnDocuments = "/divisions/a/projects/docs";
    private const string OtherDocuments = "/divisions/b/projects/docs";

    [Fact]
    public async Task MovingAFileToADeniedDestinationReturnsUnauthorized()
    {
        var media = host.Reset();
        var file = media.AddFile(OwnDocuments, "report.pdf");

        using var response = await host.Client.PutAsJsonAsync($"/media/file/{file.Id}",
            new { path = OtherDocuments, name = "report.pdf" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await media.Storage.DidNotReceive().UpdateFileAsync(Arg.Any<Guid>(), Arg.Any<FileModel>());
    }

    [Fact]
    public async Task UpdatingAFileWhoseSourceIsDeniedReturnsUnauthorized()
    {
        var media = host.Reset();
        var file = media.AddFile(OtherDocuments, "report.pdf");

        using var response = await host.Client.PutAsJsonAsync($"/media/file/{file.Id}",
            new { path = OtherDocuments, name = "report.pdf" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task MovingAFileOntoAnExistingNameReturnsConflict()
    {
        var media = host.Reset();
        var file = media.AddFile(OwnDocuments + "/drafts", "report.pdf");
        media.AddFile(OwnDocuments, "report.pdf");

        using var response = await host.Client.PutAsJsonAsync($"/media/file/{file.Id}",
            new { path = OwnDocuments, name = "report.pdf" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var errors = await response.Content.ReadFromJsonAsync<Dictionary<string, string[]>>();
        Assert.Equal([NhMediaErrorCodes.FileNameExists], errors![nameof(FileModel.Name)]);
    }

    [Fact]
    public async Task MovingAFolderToADeniedDestinationReturnsUnauthorized()
    {
        var media = host.Reset();

        using var response = await host.Client.PutAsJsonAsync("/media/folder", new
        {
            path = "/divisions/a/projects",
            folderName = "staging",
            newPath = "/divisions/b/projects",
            newName = "planted"
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await media.Storage.DidNotReceive().MoveFolderAsync(Arg.Any<string?>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task MovingAFolderOntoAnExistingFolderReturnsConflict()
    {
        var media = host.Reset();
        media.AddFolder("/divisions/a/projects", "staging");
        media.AddFolder("/divisions/a/projects", "archive");

        using var response = await host.Client.PutAsJsonAsync("/media/folder", new
        {
            path = "/divisions/a/projects",
            folderName = "staging",
            newPath = "/divisions/a/projects",
            newName = "archive"
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await media.Storage.DidNotReceive().MoveFolderAsync(Arg.Any<string?>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>());
    }

    [Theory]
    [InlineData("secret.pdf")]
    [InlineData("does-not-exist.pdf")]
    public async Task UpdatingTagsOfADeniedFileReturnsUnauthorizedWhetherOrNotItExists(string fileName)
    {
        var media = host.Reset();
        media.AddFile(OtherDocuments, "secret.pdf");

        using var response = await host.Client.PostAsJsonAsync("/media/file/tags",
            new { path = OtherDocuments, fileName, tags = new[] { "planted" } });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await media.Storage.DidNotReceive().UpdateTagsAsync(Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IEnumerable<string>>());
    }

    [Fact]
    public async Task UpdatingTagsOfAnAuthorizedFileReturnsNoContent()
    {
        var media = host.Reset();
        media.AddFile(OwnDocuments, "report.pdf");
        media.Storage.UpdateTagsAsync(OwnDocuments, "report.pdf", Arg.Any<IEnumerable<string>>())
            .Returns(TaskResult.Succeeded());

        using var response = await host.Client.PostAsJsonAsync("/media/file/tags",
            new { path = OwnDocuments, fileName = "report.pdf", tags = new[] { "quarterly" } });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task LocalizingAsAViewerReturnsUnauthorized()
    {
        var media = host.Reset();
        media.AddFile(OwnDocuments, "plan.txt");
        media.Module.Deny(decision => decision.Action != ActionType.Read);

        using var response = await host.Client.PostAsync(LocalizeUrl("plan.txt", "Title", "Defaced"), null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal([ActionType.Read, ActionType.Update], media.Module.Decisions.Select(decision => decision.Action));
        await media.Storage.DidNotReceive().LocalizeAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>());
    }

    [Fact]
    public async Task LocalizingAPropertyThatIsNoDisplayStringReturnsBadRequest()
    {
        var media = host.Reset();
        media.AddFile(OwnDocuments, "plan.txt");

        using var response = await host.Client.PostAsync(LocalizeUrl("plan.txt", "Id", "x"), null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = await response.Content.ReadFromJsonAsync<Dictionary<string, string[]>>();
        Assert.Equal([NhMediaErrorCodes.PropertyNotLocalizable], errors!["propertyName"]);
    }

    [Fact]
    public async Task LocalizingADisplayStringReturnsNoContent()
    {
        var media = host.Reset();
        var file = media.AddFile(OwnDocuments, "plan.txt");
        media.Storage.LocalizeAsync(file.Id, "nl", "title", "Projectplan").Returns(TaskResult.Succeeded());

        using var response = await host.Client.PostAsync(LocalizeUrl("plan.txt", "title", "Projectplan"), null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task AFailedLocalizationIsNotReportedAsDone()
    {
        var media = host.Reset();
        var file = media.AddFile(OwnDocuments, "plan.txt");
        media.Storage.LocalizeAsync(file.Id, "nl", "Title", "").Returns(TaskResult.Failed("Localization not found"));

        using var response = await host.Client.PostAsync(LocalizeUrl("plan.txt", "Title", ""), null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static string LocalizeUrl(string fileName, string propertyName, string value)
    {
        return "/media/file/localize" +
               $"?path={Uri.EscapeDataString(OwnDocuments)}" +
               $"&fileName={Uri.EscapeDataString(fileName)}" +
               "&language=nl" +
               $"&propertyName={Uri.EscapeDataString(propertyName)}" +
               $"&value={Uri.EscapeDataString(value)}";
    }

    /// <summary>
    /// The library's media endpoints over a <see cref="MediaLibraryService"/> with substituted storage and a
    /// module scoped to <c>/divisions/a</c>, started once for the class. Each test resets the service it
    /// talks to; the tests of one class run one after the other.
    /// </summary>
    public sealed class Host : IAsyncLifetime
    {
        private WebApplication? _app;
        private ServiceContext _current = new();
        private IMediaLibraryService? _service;

        public HttpClient Client { get; private set; } = null!;

        public ServiceContext Reset(string allowedRoot = "/divisions/a")
        {
            _current = new ServiceContext(allowedRoot);
            _service = null;
            return _current;
        }

        /// <summary>
        /// Serves the endpoints with <paramref name="service"/>, such as a consumer subclass of
        /// <see cref="MediaLibraryService"/>, until the next <see cref="Reset"/>.
        /// </summary>
        public void Serve(IMediaLibraryService service)
        {
            _service = service;
        }

        public async Task InitializeAsync()
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton(new NhMediaContext());
            builder.Services.AddTransient<IMediaLibraryService>(_ => _service ?? _current.Service);

            _app = builder.Build();
            _app.MapNhMediaEndpoints("media", configureOptions: null);
            await _app.StartAsync();
            Client = _app.GetTestClient();
        }

        public async Task DisposeAsync()
        {
            Client.Dispose();
            if (_app is not null)
            {
                await _app.StopAsync();
                await _app.DisposeAsync();
            }
        }
    }
}
