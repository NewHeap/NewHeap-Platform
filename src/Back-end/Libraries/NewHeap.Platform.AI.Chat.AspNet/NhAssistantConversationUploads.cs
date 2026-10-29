using System.Security.Claims;
using NewHeap.Platform.AI.Chat.Collaboration;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet;

/// <summary>
/// Where an upload goes once its checks before the content passed: the conversation, what people may
/// upload to its agent and the actor the file source stores it for.
/// </summary>
internal sealed record NhAssistantUploadTarget(
    Guid ConversationId,
    NhAssistantUploadRules Rules,
    NhAssistantFileActor Actor);

/// <summary>
/// A person's upload to a conversation, shared by <c>POST conversations/{id}/files</c> and
/// <see cref="INhAssistantConversations.UploadAsync"/> (FB23), so both refuse alike. Before the content
/// is read: the agent's policy, which everyone who uploads must satisfy because uploading belongs to
/// starting a turn (<c>assistant-agent-not-found</c>, <c>assistant-agent-forbidden</c>), a participant's
/// role in an agent's thread (<c>assistant-subject-access-denied</c>), a file source
/// (<c>assistant-files-unavailable</c>) and an agent that accepts files and whose profile chain may
/// receive them (<c>assistant-files-not-accepted</c>). Then <see cref="NhAssistantUploads"/> checks the
/// name, the media type against the content, the size and the scanner before the application's file
/// source stores the file. Nothing here logs a file name.
/// </summary>
internal sealed class NhAssistantConversationUploads(
    NhAssistantAgentCatalog catalog,
    NhAssistantAgentAccess agentAccess,
    NhAssistantUploads uploads)
{
    /// <summary>
    /// The checks before the content is read, for <paramref name="user"/> in the conversation of
    /// <paramref name="access"/>; <paramref name="thread"/> is the person's live access to an agent's
    /// thread, <see langword="null"/> for a chat.
    /// </summary>
    public async Task<TaskResult<NhAssistantUploadTarget>> PrepareAsync(
        ClaimsPrincipal user,
        NhAssistantConversationAccess access,
        NhAssistantThreadAccess? thread,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(access);

        var conversation = access.Conversation;
        var effective = await catalog.FindAsync(conversation.AgentId, includeDisabled: false, cancellationToken);
        if (effective is null)
        {
            return Failed(NhAssistantErrorCodes.AgentNotFound, "The assistant agent was not found.");
        }
        if (!await agentAccess.CanUseAsync(user, effective.Definition))
        {
            return Failed(NhAssistantErrorCodes.AgentForbidden, "The person may not use the assistant agent.");
        }
        if (thread is not null && thread.Role < NhAiSubjectRole.Participant)
        {
            return Failed(NhAssistantErrorCodes.SubjectAccessDenied, "The person may not take part in the agent's thread.");
        }
        if (!uploads.Available)
        {
            return Failed(NhAssistantErrorCodes.FilesUnavailable, "The application takes no uploads.");
        }

        var rules = uploads.RulesOf(effective);
        if (rules is null)
        {
            return Failed(NhAssistantErrorCodes.FilesNotAccepted, "The agent accepts no uploads.");
        }

        var actor = new NhAssistantFileActor(access.ActorId, NhAiActorKind.Human, conversation.AgentId, conversation.Id)
        {
            AccountableOwnerId = thread?.Thread.AccountableOwnerId ?? access.ActorId,
            TenantId = conversation.TenantId,
            Subject = thread?.Subject,
            // The caller checked the person's role on a chat's subject before (FB25).
            ConversationSubject = thread is null ? NhAssistantChatSubjects.SubjectOf(conversation) : null,
            Origin = NhAssistantFileOrigins.Attachment
        };
        return TaskResult<NhAssistantUploadTarget>.Succeeded(new NhAssistantUploadTarget(conversation.Id, rules, actor));
    }

    /// <summary>
    /// Checks the file against the target's rules and has the application's file source store it.
    /// </summary>
    public Task<TaskResult<NhAiFileReference>> StoreAsync(
        NhAssistantUploadTarget target,
        NhAssistantUploadInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(input);
        return uploads.StoreAsync(target.ConversationId, input, target.Rules, target.Actor, cancellationToken);
    }

    private static TaskResult<NhAssistantUploadTarget> Failed(string code, string message)
    {
        return TaskResult<NhAssistantUploadTarget>.Failed(code, message);
    }
}

/// <summary>
/// The lists a person reads, shared by <c>GET conversations</c> and <c>GET agents</c> and by
/// <see cref="INhAssistantConversations.ListAsync"/> and
/// <see cref="INhAssistantConversations.ListAgentsAsync"/> (FB23).
/// </summary>
internal static class NhAssistantPersonLists
{
    /// <summary>
    /// The default page size of the conversation list.
    /// </summary>
    public const int DefaultItemsPerPage = 20;

    /// <summary>
    /// The largest page of the conversation list.
    /// </summary>
    public const int MaxItemsPerPage = 100;

    /// <summary>
    /// The chats <paramref name="caller"/> owns or takes part in, most recently updated first, with the
    /// caller's read state and the subject of a chat about one (FB25); <c>assistant-message-invalid</c> for
    /// a page below 1 or a page size outside 1 to <see cref="MaxItemsPerPage"/>. The list asks no subject's
    /// resolver: opening a chat does.
    /// </summary>
    public static async Task<TaskResult<NhAssistantConversationListDto>> ListConversationsAsync(
        INhAssistantStore store,
        NhAssistantSubjectAccessService subjects,
        NhAiInvocationContext caller,
        int page,
        int itemsPerPage,
        CancellationToken cancellationToken)
    {
        if (page < 1 || itemsPerPage is < 1 or > MaxItemsPerPage)
        {
            return TaskResult<NhAssistantConversationListDto>.Failed(
                NhAssistantErrorCodes.MessageInvalid,
                "The page of the conversation list is not valid.");
        }

        var (items, total) = await store.ListConversationsAsync(
            caller.ActorId,
            caller.TenantId,
            page,
            itemsPerPage,
            cancellationToken);
        return TaskResult<NhAssistantConversationListDto>.Succeeded(
            new NhAssistantConversationListDto(items.Select(item => NhAssistantDtoMapper.ToSummary(item, subjects)).ToArray(), total));
    }

    /// <summary>
    /// The agents offered in the chat whose policy <paramref name="user"/> satisfies, each with what
    /// people may attach to a message to it: for a chat without a subject those that do not require one
    /// (<c>RequireChatSubject()</c>), for a chat about <paramref name="subject"/> (FB25) those that work on
    /// it. The caller checked the person's access to the subject.
    /// </summary>
    public static async Task<IReadOnlyList<NhAssistantAgentSummaryDto>> ListAgentsAsync(
        NhAssistantAgentAccess access,
        NhAssistantUploads uploads,
        ClaimsPrincipal user,
        CancellationToken cancellationToken,
        NhAiSubjectRef? subject = null)
    {
        var agents = await access.GetVisibleAsync(user, cancellationToken);
        return agents
            .Where(agent => NhAssistantChatSubjects.IsOffered(agent, subject))
            .Select(agent => NhAssistantDtoMapper.ToDto(agent, uploads))
            .ToArray();
    }

    /// <summary>
    /// The subject of <c>GET agents?subjectType=&amp;subjectId=</c> and of
    /// <see cref="INhAssistantConversations.ListAgentsAsync(NhAssistantDecidingPerson, NhAiSubjectRef, NhAssistantConversationOptions?, CancellationToken)"/>
    /// for <paramref name="caller"/>: an invalid one is <c>assistant-subject-invalid</c>, one of a type
    /// without a resolver <c>assistant-subject-type-unknown</c>, and one the caller may not use a chat
    /// about answers as <see cref="NhAssistantChatSubjects.CheckAsync(NhAiSubjectRef, string, string?, CancellationToken)"/>.
    /// </summary>
    public static async Task<TaskResult<NhAiSubjectRef>> CheckAgentsSubjectAsync(
        NhAssistantSubjectAccessService subjects,
        NhAssistantChatSubjects chatSubjects,
        NhAiInvocationContext caller,
        NhAiSubjectRef subject,
        CancellationToken cancellationToken)
    {
        if (NhAiSubjectRef.IsReservedType(subject.Type))
        {
            return TaskResult<NhAiSubjectRef>.Failed(NhAssistantErrorCodes.SubjectInvalid, "The chat's subject is not valid.");
        }
        if (!subjects.IsKnownType(subject.Type))
        {
            return TaskResult<NhAiSubjectRef>.Failed(NhAssistantErrorCodes.SubjectTypeUnknown, "The chat's subject type is not registered.");
        }

        var checkedSubject = await chatSubjects.CheckAsync(subject, caller.ActorId, caller.TenantId, cancellationToken);
        return checkedSubject.Success
            ? TaskResult<NhAiSubjectRef>.Succeeded(subject)
            : TaskResult<NhAiSubjectRef>.Failed(checkedSubject);
    }
}
