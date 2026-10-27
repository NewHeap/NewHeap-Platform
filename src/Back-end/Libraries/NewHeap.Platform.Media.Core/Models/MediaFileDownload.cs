using NewHeap.Media.Modules;

namespace NewHeap.Media.Models;

/// <summary>
/// A file opened for download: its content together with the stored reference that decides how it is
/// served. <see cref="ContentType"/> and <see cref="Inline"/> come from the stored name through
/// <see cref="NhMediaContentTypes"/>, never from a name a client sent. Content without a stored
/// reference is served as <see cref="NhMediaContentTypes.Default"/>, as an attachment.
/// </summary>
public sealed class MediaFileDownload : IDisposable
{
    /// <summary>
    /// Content of a stored file; its stored name is the download name and decides the content type.
    /// </summary>
    public MediaFileDownload(FileReference file, Stream content)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(content);

        File = file;
        FileName = file.Name;
        Content = content;
        ContentType = NhMediaContentTypes.FromFileName(file.Name);
        Inline = NhMediaContentTypes.IsInlineSafe(ContentType);
    }

    /// <summary>
    /// Content without a stored reference, for example from an <see cref="IMediaLibraryService"/> that
    /// serves a file its storage does not list. It is served as <see cref="NhMediaContentTypes.Default"/>,
    /// as an attachment named <paramref name="fileName"/>.
    /// </summary>
    public MediaFileDownload(string fileName, Stream content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(content);

        FileName = fileName;
        Content = content;
        ContentType = NhMediaContentTypes.Default;
        Inline = false;
    }

    /// <summary>
    /// The stored reference of the file, or <see langword="null"/> when the content has none.
    /// </summary>
    public FileReference? File { get; }

    /// <summary>
    /// The download name: the stored name when the file has a stored reference.
    /// </summary>
    public string FileName { get; }

    /// <summary>
    /// The file's bytes. Whoever writes them to a response owns the stream.
    /// </summary>
    public Stream Content { get; }

    /// <summary>
    /// The content type to serve the file with.
    /// </summary>
    public string ContentType { get; }

    /// <summary>
    /// Whether the file may be served inline; otherwise serve it as an attachment.
    /// </summary>
    public bool Inline { get; }

    public void Dispose()
    {
        Content.Dispose();
    }
}
