using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NewHeap.Media;
using NewHeap.Media.Modules;
using NewHeap.Platform.Common.Models;
using NSubstitute;
using Xunit;

namespace NewHeap.Platform.Media.Tests;

/// <summary>
/// The download endpoint serves stored bytes with a content type the server chooses from the stored file
/// name through a fixed list, never with the name a request sends, and with <c>X-Content-Type-Options:
/// nosniff</c>. Only raster images are served inline; every other file is an attachment, so a browser
/// never renders an uploaded document on the API origin.
/// </summary>
public sealed class MediaDownloadEndpointTests(MediaEndpointAuthorizationTests.Host host)
    : IClassFixture<MediaEndpointAuthorizationTests.Host>
{
    private const string Documents = "/divisions/a/projects/docs";

    private static readonly byte[] Script =
        Encoding.UTF8.GetBytes("<html><body><script>document.title='ran'</script></body></html>");

    [Theory]
    [InlineData("page.html")]
    [InlineData("page.htm")]
    [InlineData("texthtml")]
    [InlineData("script.js")]
    [InlineData("feed.xml")]
    public async Task AnActiveDocumentIsAnOctetStreamAttachment(string fileName)
    {
        var media = host.Reset();
        media.AddFile(Documents, fileName, Script);

        using var response = await host.Client.GetAsync(DownloadUrl(fileName));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(["nosniff"], response.Headers.GetValues("X-Content-Type-Options"));
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal(fileName, response.Content.Headers.ContentDisposition?.FileNameStar);
        Assert.Equal(Script, await response.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("photo.png", "image/png")]
    [InlineData("photo.JPG", "image/jpeg")]
    [InlineData("photo.jpeg", "image/jpeg")]
    [InlineData("animation.gif", "image/gif")]
    [InlineData("photo.webp", "image/webp")]
    [InlineData("photo.avif", "image/avif")]
    public async Task ARasterImageIsServedInlineWithItsType(string fileName, string contentType)
    {
        var media = host.Reset();
        media.AddFile(Documents, fileName, Script);

        using var response = await host.Client.GetAsync(DownloadUrl(fileName));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(contentType, response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(["nosniff"], response.Headers.GetValues("X-Content-Type-Options"));
        Assert.Equal("inline", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal(fileName, response.Content.Headers.ContentDisposition?.FileNameStar);
        Assert.Equal(Script, await response.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("logo.svg", "image/svg+xml")]
    [InlineData("report.pdf", "application/pdf")]
    [InlineData("notes.txt", "text/plain")]
    [InlineData("export.csv", "text/csv")]
    [InlineData("plan.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [InlineData("archive.zip", "application/zip")]
    [InlineData("clip.mp4", "video/mp4")]
    public async Task AKnownDocumentKeepsItsTypeAsAnAttachment(string fileName, string contentType)
    {
        var media = host.Reset();
        media.AddFile(Documents, fileName, Script);

        using var response = await host.Client.GetAsync(DownloadUrl(fileName));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(contentType, response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(["nosniff"], response.Headers.GetValues("X-Content-Type-Options"));
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal(fileName, response.Content.Headers.ContentDisposition?.FileNameStar);
    }

    [Fact]
    public async Task TheStoredNameDecidesTheTypeAndTheDownloadName()
    {
        var media = host.Reset();
        var file = media.AddFile(Documents, "report.pdf", Script);
        // A provider whose name comparison ignores case finds the stored file under another spelling.
        media.Storage.GetFileAsync(Documents, "REPORT.HTML", null).Returns(file);

        using var response = await host.Client.GetAsync(DownloadUrl("REPORT.HTML"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("report.pdf", response.Content.Headers.ContentDisposition?.FileNameStar);
    }

    [Fact]
    public async Task ADeniedDownloadReturnsUnauthorizedWithoutReadingTheFile()
    {
        var media = host.Reset();
        media.AddFile("/divisions/b/projects/docs", "page.html", Script);

        using var response = await host.Client.GetAsync(
            $"/media/download?path={Uri.EscapeDataString("/divisions/b/projects/docs")}&fileName=page.html");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await media.Blobs.DidNotReceive().GetFileAsync(Arg.Any<Guid>());
    }

    [Fact]
    public async Task AMissingFileReturnsNotFound()
    {
        host.Reset();

        using var response = await host.Client.GetAsync(DownloadUrl("missing.png"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AnOverrideOfDownloadFileAsyncStillDecidesTheDownload()
    {
        var media = host.Reset();
        media.AddFile(Documents, "photo.png", Script);
        media.AddFile(Documents, "secret.png", Script);
        var watermarked = Encoding.UTF8.GetBytes("watermarked");
        var service = new CheckedMediaLibraryService(media, refused: "secret.png", replacement: watermarked);
        host.Serve(service);

        using var refused = await host.Client.GetAsync(DownloadUrl("secret.png"));
        using var served = await host.Client.GetAsync(DownloadUrl("photo.png"));

        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal(watermarked, await served.Content.ReadAsByteArrayAsync());
        Assert.Equal("image/png", served.Content.Headers.ContentType?.MediaType);
        Assert.Equal("inline", served.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal(["nosniff"], served.Headers.GetValues("X-Content-Type-Options"));
        Assert.Equal(["secret.png", "photo.png"], service.Requested);
    }

    [Fact]
    public async Task ContentWithoutAStoredReferenceIsAnOctetStreamAttachment()
    {
        var media = host.Reset();
        var service = new CheckedMediaLibraryService(media, refused: null,
            replacement: Encoding.UTF8.GetBytes("generated"));
        host.Serve(service);

        // The override serves a file the storage does not list, so nothing stored names its type.
        using var response = await host.Client.GetAsync(DownloadUrl("report.png"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("report.png", response.Content.Headers.ContentDisposition?.FileNameStar);
        Assert.Equal(["nosniff"], response.Headers.GetValues("X-Content-Type-Options"));
    }

    [Fact]
    public async Task OpeningADownloadAuthorizesOneReadAndReturnsTheStoredReference()
    {
        var media = host.Reset();
        var file = media.AddFile(Documents, "photo.png", Script);

        using var download = await media.Service.OpenDownloadAsync(Documents, "photo.png");

        Assert.True(download.Success);
        Assert.Same(file, download.Data.File);
        Assert.Equal("image/png", download.Data.ContentType);
        Assert.True(download.Data.Inline);
        Assert.Equal([new MediaLibraryMutationAuthorizationTests.Decision(ActionType.Read, Documents, "photo.png")],
            media.Module.Decisions);
    }

    [Theory]
    [InlineData(null, "application/octet-stream")]
    [InlineData("", "application/octet-stream")]
    [InlineData("README", "application/octet-stream")]
    [InlineData("photo.", "application/octet-stream")]
    [InlineData("photo.png ", "application/octet-stream")]
    [InlineData("page.html", "application/octet-stream")]
    [InlineData("page.xhtml", "application/octet-stream")]
    [InlineData("module.mjs", "application/octet-stream")]
    [InlineData("photo.png.html", "application/octet-stream")]
    [InlineData("page.html.png", "image/png")]
    [InlineData("Photo.PNG", "image/png")]
    [InlineData("logo.svg", "image/svg+xml")]
    [InlineData("planning.pdf", "application/pdf")]
    public void ContentTypesComeFromTheListedExtension(string? fileName, string contentType)
    {
        Assert.Equal(contentType, NhMediaContentTypes.FromFileName(fileName));
    }

    [Theory]
    [InlineData("image/png", true)]
    [InlineData("IMAGE/JPEG", true)]
    [InlineData("image/svg+xml", false)]
    [InlineData("application/pdf", false)]
    [InlineData("text/plain", false)]
    [InlineData("application/octet-stream", false)]
    [InlineData(null, false)]
    public void OnlyRasterImagesAreInlineSafe(string? contentType, bool inline)
    {
        Assert.Equal(inline, NhMediaContentTypes.IsInlineSafe(contentType));
    }

    private static string DownloadUrl(string fileName)
    {
        return $"/media/download?path={Uri.EscapeDataString(Documents)}&fileName={Uri.EscapeDataString(fileName)}";
    }

    /// <summary>
    /// A consumer subclass that adds its own check to <see cref="MediaLibraryService.DownloadFileAsync(string?, string)"/>
    /// and replaces the bytes it serves, as a watermarking or scanning service would.
    /// </summary>
    private sealed class CheckedMediaLibraryService(
        MediaLibraryMutationAuthorizationTests.ServiceContext context,
        string? refused,
        byte[] replacement)
        : MediaLibraryService([], new ThumbnailService(), context.Storage, context.Blobs, context.Module,
            NullLogger<MediaLibraryService>.Instance)
    {
        public List<string> Requested { get; } = [];

        public override Task<DisposableTaskResult<Stream>> DownloadFileAsync(string? path, string fileName)
        {
            Requested.Add(fileName);
            if (fileName == refused)
            {
                throw new UnauthorizedAccessException();
            }

            return Task.FromResult<DisposableTaskResult<Stream>>(new MemoryStream(replacement));
        }
    }
}
