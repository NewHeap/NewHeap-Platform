using System.Text.RegularExpressions;

namespace NewHeap.Platform.AI;

/// <summary>
/// A file the application keeps, as a tool result or a chat message names it: the application's own
/// id and what the application reports about the file. It never carries content. The assistant opens
/// the file through the application's file source when a turn needs it, with the rights of that
/// turn, and records only this reference and the hash of what it read.
/// </summary>
/// <param name="Id">The application's id of the file, at most <see cref="MaxIdLength"/> characters.</param>
/// <param name="Name">The file name people see, at most <see cref="MaxNameLength"/> characters.</param>
/// <param name="MediaType">The media type as the application reports it, such as <c>application/pdf</c>;
/// see <see cref="NhAiMediaTypes"/>.</param>
/// <param name="Size">The size in bytes as the application reports it.</param>
/// <param name="Hash">The application's hash of the content, such as <c>sha256:&lt;hex&gt;</c>, at most
/// <see cref="MaxHashLength"/> characters; <see langword="null"/> when the application keeps none.</param>
public sealed record NhAiFileReference(
    string Id,
    string Name,
    string MediaType,
    long Size,
    string? Hash = null)
{
    public const int MaxIdLength = 256;

    public const int MaxNameLength = 256;

    public const int MaxMediaTypeLength = 128;

    public const int MaxHashLength = 128;

    /// <summary>
    /// Whether <paramref name="reference"/> names a file within the bounds of a reference: an id and
    /// a name without control characters, a valid media type, a size of zero or more and a bounded
    /// hash.
    /// </summary>
    public static bool IsValid(NhAiFileReference? reference)
    {
        return reference is not null
            && IsText(reference.Id, MaxIdLength)
            && IsText(reference.Name, MaxNameLength)
            && NhAiMediaTypes.IsValid(reference.MediaType)
            && reference.Size >= 0
            && (reference.Hash is null || IsText(reference.Hash, MaxHashLength));
    }

    private static bool IsText(string? value, int maximum)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Length <= maximum
            && !value.Any(char.IsControl);
    }
}

/// <summary>
/// The media types the platform names, and the check a media type passes before a profile, a file
/// source or a tool may use it. Media types are compared lowercase and without parameters.
/// </summary>
public static partial class NhAiMediaTypes
{
    public const string Pdf = "application/pdf";

    public const string Png = "image/png";

    public const string Jpeg = "image/jpeg";

    /// <summary>A Word document (<c>.docx</c>), which models read through a reading tool.</summary>
    public const string Docx = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    /// <summary>An Excel workbook (<c>.xlsx</c>), which models read through a reading tool.</summary>
    public const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <summary>
    /// The form <c>type/subtype</c>. The pattern is linear and the value at most
    /// <see cref="NhAiFileReference.MaxMediaTypeLength"/> characters, so it has no match timeout: a
    /// busy machine never turns a valid media type into a failure.
    /// </summary>
    [GeneratedRegex("^[a-z0-9][a-z0-9!#$&^_.+-]*/[a-z0-9][a-z0-9!#$&^_.+-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    /// <summary>
    /// The media type in lowercase without parameters, such as <c>text/plain</c> for
    /// <c>Text/Plain; charset=utf-8</c>; <see langword="null"/> for a value that is not a media type.
    /// </summary>
    public static string? Normalize(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            return null;
        }

        var separator = mediaType.IndexOf(';', StringComparison.Ordinal);
        var essence = (separator < 0 ? mediaType : mediaType[..separator]).Trim().ToLowerInvariant();
        return essence.Length <= NhAiFileReference.MaxMediaTypeLength && Pattern().IsMatch(essence)
            ? essence
            : null;
    }

    /// <summary>
    /// Whether <paramref name="mediaType"/> is a media type of the form <c>type/subtype</c>.
    /// </summary>
    public static bool IsValid(string? mediaType)
    {
        return Normalize(mediaType) is not null;
    }
}

/// <summary>
/// The data of a governed tool result that names files. <see cref="NhAiToolResult.Create{T}(NewHeap.Platform.Common.Models.TaskResult{T}, System.Text.Json.JsonSerializerOptions)"/>
/// writes them as the result's <c>files</c> next to <c>data</c>, so the assistant can open them
/// through the application's file source with the rights of the turn and give them to the model.
/// Mark the property <c>[JsonIgnore]</c> to keep the references out of <c>data</c> itself.
/// </summary>
public interface INhAiToolResultFiles
{
    /// <summary>
    /// The files the result names, at most <see cref="NhAiToolResult.MaxFiles"/>.
    /// </summary>
    IReadOnlyList<NhAiFileReference> Files { get; }
}
