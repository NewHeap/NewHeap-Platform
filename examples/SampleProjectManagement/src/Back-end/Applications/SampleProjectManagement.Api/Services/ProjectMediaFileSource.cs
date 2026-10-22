using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using NewHeap.Media;
using NewHeap.Media.Models;
using NewHeap.Media.Modules;
using NewHeap.Platform.AI;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.Common.Models;
using SampleProjectManagement.Core.Services;
using SampleProjectManagement.DAL;

namespace SampleProjectManagement.Api.Services;

/// <summary>
/// SPM-305, SPM-306: the assistant's files on the sample's media library. The assistant keeps only
/// references and hashes; this source decides for every turn whether its actor may read a media item
/// and how sensitive it is. Two kinds of media items reach a model:
/// <list type="bullet">
/// <item>files people upload to a conversation, stored in a folder of that conversation and opened
/// only in turns of that conversation, whose participants the assistant already checked;</item>
/// <item>the documents of a project, opened for people who may see the project's agent work and for
/// an agent that works on that project.</item>
/// </list>
/// Every other media item is refused. Id lookups of the media library skip its path-based
/// <see cref="IAuthorizationModule"/>, which needs an HTTP request that agent runs do not have, so
/// this source checks access itself before it downloads anything.
/// </summary>
public sealed class ProjectMediaFileSource(
    IMediaLibraryService mediaLibrary,
    IMediaStorage mediaStorage,
    IFileStructureStorage fileStructure,
    IEnumerable<INhAiSubjectAccessResolver> subjectAccess,
    SampleProjectManagementDbContext dbContext) : INhAssistantFileSource
{
    public async Task<TaskResult<NhAssistantFile>> OpenAsync(
        NhAiFileReference reference,
        NhAssistantFileActor actor,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(reference.Id, out var id))
        {
            return Refused(NhAssistantErrorCodes.FileNotFound);
        }

        var found = await mediaLibrary.GetFileAsync(id);
        if (!found.Success || found.Data is null)
        {
            return Refused(NhAssistantErrorCodes.FileNotFound);
        }

        var file = found.Data;
        if (!await MayReadAsync(file, actor, cancellationToken))
        {
            return Refused(NhAssistantErrorCodes.FileForbidden);
        }

        Stream content;
        using (var download = await mediaLibrary.DownloadFileAsync(id))
        {
            if (!download.Success || download.Data is null)
            {
                return Refused(NhAssistantErrorCodes.FileNotFound);
            }

            // The assistant disposes the file, and with it the stream.
            content = download.Data;
            download.Data = null;
        }

        long length;
        if (ProjectMediaMetadata.LengthOf(file) is { } stored)
        {
            length = stored;
        }
        else
        {
            (content, length) = await MeasureAsync(content, cancellationToken);
        }

        return TaskResult<NhAssistantFile>.Succeeded(new NhAssistantFile(
            content,
            ProjectMediaMetadata.MediaTypeOf(file),
            length,
            ProjectMediaMetadata.ClassificationOf(file))
        {
            PageCount = ProjectMediaMetadata.PageCountOf(file)
        });
    }

    /// <summary>
    /// Stores a chat upload in the media library, in the folder of its conversation, with the
    /// classification the assistant gave it (<c>Confidential</c> by default), its name and media type.
    /// The assistant checked the conversation, the agent, the media type against the content and the
    /// size before this call; an application that scans uploads refuses a file here with a failed
    /// result. The media library's own endpoints never serve the folder: its authorization module
    /// grants project folders of a division only.
    /// </summary>
    public async Task<TaskResult<NhAiFileReference>> StoreUploadAsync(
        NhAssistantFileUpload upload,
        NhAssistantFileActor actor,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var model = new FileModel
        {
            Path = ProjectMediaFolders.UploadsOf(upload.ConversationId),
            Name = upload.Name,
            Creator = actor.ActorId,
            MetaData = ProjectMediaMetadata.Create(upload.MediaType, upload.Length, upload.Classification)
        };

        // The storage modules of the media library, as IMediaLibraryService.CreateFileAsync uses them,
        // without its path-based authorization module: the assistant authorized the upload already.
        var storageId = await mediaStorage.SaveFileAsync(upload.Content);
        var created = await fileStructure.CreateFileAsync(model, storageId);
        if (created is not { Success: true, Data: not null })
        {
            await mediaStorage.DeleteAsync(storageId);
            return TaskResult<NhAiFileReference>.Failed(NhAssistantErrorCodes.FileUploadFailed, "The media library did not store the upload.");
        }

        return TaskResult<NhAiFileReference>.Succeeded(
            new NhAiFileReference(created.Data.Id.ToString("D"), upload.Name, upload.MediaType, upload.Length));
    }

    private async Task<bool> MayReadAsync(FileReference file, NhAssistantFileActor actor, CancellationToken cancellationToken)
    {
        if (ProjectMediaFolders.TryGetUploadConversation(file.Folder.FullPath, out var conversationId))
        {
            // An upload belongs to its conversation; a later turn of it opens the file again.
            return conversationId == actor.ConversationId;
        }

        if (!ProjectMediaFolders.TryGetProjectDocuments(file.Folder.FullPath, out var divisionId, out var projectId))
        {
            return false;
        }

        var inDivision = await dbContext.Projects
            .AsNoTracking()
            .AnyAsync(project => project.Id == projectId && project.DivisionId == divisionId, cancellationToken);
        if (!inDivision)
        {
            return false;
        }

        var subject = ProjectSubjects.For(projectId);
        if (actor.ActorKind == NhAiActorKind.Agent)
        {
            // An agent that answers for its own run reads the documents of the project it works on.
            return actor.Subject == subject;
        }

        // A person reads a project's documents when they may see the project's agent work: the
        // owner, its members and people who read every project.
        var projects = subjectAccess.First(resolver => resolver.SubjectType == ProjectSubjects.Type);
        var access = await projects.ResolveAsync(subject, actor.ActorId, cancellationToken);
        return access.Has(NhAiSubjectRole.Reader);
    }

    /// <summary>
    /// The length of a stream the media library keeps no length for. The sample stores files on the
    /// file system, whose streams know it; a stream that does not is read once into memory.
    /// </summary>
    private static async Task<(Stream Content, long Length)> MeasureAsync(Stream content, CancellationToken cancellationToken)
    {
        if (content.CanSeek)
        {
            return (content, content.Length - content.Position);
        }

        var buffered = new MemoryStream();
        await using (content)
        {
            await content.CopyToAsync(buffered, cancellationToken);
        }

        buffered.Position = 0;
        return (buffered, buffered.Length);
    }

    private static TaskResult<NhAssistantFile> Refused(string code)
    {
        return TaskResult<NhAssistantFile>.Failed(code, "The media item is not available to this turn.");
    }
}

/// <summary>
/// SPM-306: the documents of a project in the media library, for the document tool. The tool already
/// checked the project in the authorized division; an agent run has no HTTP request for the media
/// library's path-based authorization module, so the folder is read from its file structure.
/// </summary>
public sealed class ProjectMediaDocuments(IFileStructureStorage fileStructure) : IProjectAiDocumentService
{
    public async Task<IReadOnlyList<NhAiFileReference>> ListForAiAsync(
        Guid divisionId,
        Guid projectId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var folder = await fileStructure.GetFolderAsync(
            ProjectMediaFolders.DocumentsOf(divisionId, projectId),
            null,
            new FileGetOptions
            {
                PageSize = limit,
                OrderBy = [new SortOption { Key = nameof(FileReference.CreationDateTime), Direction = Direction.Descending }]
            });

        return folder.Files.Select(ProjectMediaMetadata.ReferenceOf).ToArray();
    }
}

/// <summary>
/// The media library folders the assistant reads: the uploads of a conversation and the documents
/// of a project.
/// </summary>
public static partial class ProjectMediaFolders
{
    public const string UploadsRoot = "/assistant-uploads";

    public static string UploadsOf(Guid conversationId)
    {
        return $"{UploadsRoot}/{conversationId:N}";
    }

    public static string DocumentsOf(Guid divisionId, Guid projectId)
    {
        return $"/divisions/{divisionId:D}/projects/{projectId:D}/documents";
    }

    public static bool TryGetUploadConversation(string? folderPath, out Guid conversationId)
    {
        conversationId = Guid.Empty;
        var match = UploadsPattern().Match(folderPath ?? string.Empty);
        return match.Success && Guid.TryParseExact(match.Groups["conversation"].Value, "N", out conversationId);
    }

    public static bool TryGetProjectDocuments(string? folderPath, out Guid divisionId, out Guid projectId)
    {
        divisionId = Guid.Empty;
        projectId = Guid.Empty;
        var match = DocumentsPattern().Match(folderPath ?? string.Empty);
        return match.Success
            && Guid.TryParse(match.Groups["division"].Value, out divisionId)
            && Guid.TryParse(match.Groups["project"].Value, out projectId);
    }

    [GeneratedRegex("^/assistant-uploads/(?<conversation>[0-9a-f]{32})$", RegexOptions.IgnoreCase)]
    private static partial Regex UploadsPattern();

    [GeneratedRegex("^/divisions/(?<division>[0-9a-f-]{36})/projects/(?<project>[0-9a-f-]{36})/documents$", RegexOptions.IgnoreCase)]
    private static partial Regex DocumentsPattern();
}

/// <summary>
/// What the sample stores with a media item for the assistant: its media type, length and data
/// classification. The media library keeps none of them itself; a document without them, such as one
/// uploaded in the media playground, gets the media type of its extension and is internal.
/// </summary>
public static class ProjectMediaMetadata
{
    public const string MediaTypeKey = "mediaType";
    public const string LengthKey = "length";
    public const string ClassificationKey = "classification";
    public const string PageCountKey = "pageCount";

    public static Dictionary<string, object> Create(string mediaType, long length, NhAiDataClassification classification)
    {
        return new Dictionary<string, object>
        {
            [MediaTypeKey] = mediaType,
            [LengthKey] = length,
            [ClassificationKey] = classification.ToString()
        };
    }

    public static NhAiFileReference ReferenceOf(FileReference file)
    {
        var name = file.Name.Length > NhAiFileReference.MaxNameLength
            ? file.Name[..NhAiFileReference.MaxNameLength]
            : file.Name;
        // The size as far as the sample knows it; the file source reports the real length when it opens the file.
        return new NhAiFileReference(file.Id.ToString("D"), name, MediaTypeOf(file), LengthOf(file) ?? 0);
    }

    public static string MediaTypeOf(FileReference file)
    {
        return NhAiMediaTypes.Normalize(Text(file, MediaTypeKey))
            ?? ProjectMediaSampleService.ResolveContentType(file.Name);
    }

    public static long? LengthOf(FileReference file)
    {
        return Number(file, LengthKey);
    }

    public static int? PageCountOf(FileReference file)
    {
        var pages = Number(file, PageCountKey);
        if (pages is null || pages < 1 || pages > int.MaxValue)
        {
            return null;
        }

        return (int)pages.Value;
    }

    public static NhAiDataClassification ClassificationOf(FileReference file)
    {
        if (Enum.TryParse<NhAiDataClassification>(Text(file, ClassificationKey), ignoreCase: true, out var classification)
            && Enum.IsDefined(classification))
        {
            return classification;
        }

        return NhAiDataClassification.Internal;
    }

    private static string? Text(FileReference file, string key)
    {
        return file.MetaData?.GetValueOrDefault(key) switch
        {
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            string value => value,
            _ => null
        };
    }

    private static long? Number(FileReference file, string key)
    {
        return file.MetaData?.GetValueOrDefault(key) switch
        {
            JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetInt64(out var value) => value,
            long value => value,
            int value => value,
            _ => null
        };
    }
}
