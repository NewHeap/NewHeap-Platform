namespace NewHeap.Media;

/// <summary>
/// Failure codes that <see cref="IMediaLibraryService"/> puts in a failed result for expected outcomes, so
/// callers and the media HTTP endpoints can tell them apart from a missing file or folder.
/// </summary>
public static class NhMediaErrorCodes
{
    /// <summary>
    /// A file move or rename was refused because its destination folder already holds a file with that name.
    /// </summary>
    public const string FileNameExists = "media.file.name-exists";

    /// <summary>
    /// A folder move or rename was refused because its destination already holds a folder with that name.
    /// </summary>
    public const string FolderNameExists = "media.folder.name-exists";
}
