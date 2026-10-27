namespace NewHeap.Media;

/// <summary>
/// The content types the media library serves stored files with. The type of a file comes from the
/// extension of its stored name through a fixed server-side list; a name with any other extension, or
/// none, is served as <see cref="Default"/>. Document types that a browser runs scripts in, such as HTML,
/// XHTML, XML and JavaScript, are deliberately not on the list.
/// </summary>
/// <remarks>
/// The extension only labels the bytes; it does not make them safe. Serve every file with
/// <c>X-Content-Type-Options: nosniff</c> and as an attachment unless <see cref="IsInlineSafe"/> allows
/// the type inline, as the media download endpoint does.
/// </remarks>
public static class NhMediaContentTypes
{
    /// <summary>
    /// The type of a file whose extension is not on the list.
    /// </summary>
    public const string Default = "application/octet-stream";

    private static readonly IReadOnlyDictionary<string, string> TypesByExtension =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".avif"] = "image/avif",
            [".bmp"] = "image/bmp",
            [".gif"] = "image/gif",
            [".ico"] = "image/x-icon",
            [".jpeg"] = "image/jpeg",
            [".jpg"] = "image/jpeg",
            [".png"] = "image/png",
            [".svg"] = "image/svg+xml",
            [".tif"] = "image/tiff",
            [".tiff"] = "image/tiff",
            [".webp"] = "image/webp",
            [".pdf"] = "application/pdf",
            [".txt"] = "text/plain",
            [".csv"] = "text/csv",
            [".md"] = "text/markdown",
            [".json"] = "application/json",
            [".rtf"] = "application/rtf",
            [".doc"] = "application/msword",
            [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            [".xls"] = "application/vnd.ms-excel",
            [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            [".ppt"] = "application/vnd.ms-powerpoint",
            [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            [".odt"] = "application/vnd.oasis.opendocument.text",
            [".ods"] = "application/vnd.oasis.opendocument.spreadsheet",
            [".odp"] = "application/vnd.oasis.opendocument.presentation",
            [".zip"] = "application/zip",
            [".mp3"] = "audio/mpeg",
            [".m4a"] = "audio/mp4",
            [".ogg"] = "audio/ogg",
            [".wav"] = "audio/wav",
            [".mp4"] = "video/mp4",
            [".mov"] = "video/quicktime",
            [".webm"] = "video/webm"
        };

    // Raster images only. A browser that opens one of these with nosniff shows an image or nothing; it
    // never runs a script from it. SVG and PDF can carry scripts or active content and stay attachments.
    private static readonly HashSet<string> InlineSafeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/avif",
        "image/bmp",
        "image/gif",
        "image/jpeg",
        "image/png",
        "image/webp"
    };

    /// <summary>
    /// Returns the content type for a stored file name: the listed type of its last extension, or
    /// <see cref="Default"/>.
    /// </summary>
    /// <param name="fileName">The stored name of the file, not a name a client sent.</param>
    public static string FromFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return Default;
        }

        var extension = Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(extension))
        {
            return Default;
        }

        return TypesByExtension.TryGetValue(extension, out var contentType)
            ? contentType
            : Default;
    }

    /// <summary>
    /// Returns whether a file of this content type may be served inline: true only for the raster image
    /// types on the list. Serve every other type as an attachment.
    /// </summary>
    public static bool IsInlineSafe(string? contentType)
    {
        return contentType is not null && InlineSafeTypes.Contains(contentType);
    }
}
