using NewHeap.Media.Models;
using NewHeap.Media.Modules;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.Common.Models;

namespace SampleProjectManagement.Api.Services;

/// <summary>
/// SPM-359: the uploads of a conversation go with it. <see cref="ProjectMediaFileSource"/> stores every
/// upload in the media library folder of its conversation; once the owner archived the chat, or subject
/// retention deleted an agent's thread, the assistant calls this retention, which removes the folder's
/// media items and their content. It uses the media library's storage modules, as the file source does,
/// because no person's request is there to pass its path-based authorization module. Forgetting a
/// conversation again finds no folder and succeeds, so it is idempotent, and the assistant only warns
/// when it fails: the archive stays.
/// </summary>
public sealed class ProjectMediaUploadRetention(
    IFileStructureStorage fileStructure,
    IMediaStorage mediaStorage) : INhAssistantFileRetention
{
    public async Task<TaskResult> ForgetConversationAsync(
        NhAssistantForgottenConversation conversation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        cancellationToken.ThrowIfCancellationRequested();

        var folder = ProjectMediaFolders.UploadsOf(conversation.ConversationId);
        var uploads = await fileStructure.SearchAsync(
            string.Empty,
            folder,
            new SearchOptions { PageIndex = 0, PageSize = int.MaxValue, IncludeTotalCount = false });
        var files = uploads.Results.ToArray();

        // The content first, then the media items and the folder: when a deletion fails half way, the
        // application's next call still finds the items whose content is left.
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await mediaStorage.DeleteAsync(file.Id);
        }

        await fileStructure.DeleteFolderAsync(ProjectMediaFolders.UploadsRoot, conversation.ConversationId.ToString("N"));
        return TaskResult.Succeeded();
    }
}
