using NewHeap.Media.Modules;

namespace NewHeap.Media.Models;

/// <summary>
/// A stored file opened for download: its content together with the stored reference that decides how
/// it is served. <see cref="ContentType"/> and <see cref="Inline"/> come from the stored name through
/// <see cref="NhMediaContentTypes"/>, never from a name a client sent.
/// </summary>
public sealed class MediaFileDownload : IDisposable
{
    public MediaFileDownload(FileReference file, Stream content)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(content);

        File = file;
        Content = content;
        ContentType = NhMediaContentTypes.FromFileName(file.Name);
        Inline = NhMediaContentTypes.IsInlineSafe(ContentType);
    }

    /// <summary>
    /// The stored reference of the file; its <see cref="FileReference.Name"/> is the download name.
    /// </summary>
    public FileReference File { get; }

    /// <summary>
    /// The stored bytes. Whoever writes them to a response owns the stream.
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
