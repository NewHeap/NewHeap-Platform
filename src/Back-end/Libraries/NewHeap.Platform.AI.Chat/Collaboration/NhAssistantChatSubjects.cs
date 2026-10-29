using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.Collaboration;

/// <summary>
/// The subject of a person's chat (FB25). A new chat names it with a type and an id; the chat is about
/// it when the type's access resolver describes it in the person's tenant, gives the person at least
/// <see cref="MinimumRole"/> on it and the agent works on it. Every turn, read and invitation asks the
/// resolver again, so revoked access takes effect at once. The chat stays the person's: it never
/// becomes the agent's thread about the subject and shares no memory, run, proposal or member with it.
/// </summary>
internal sealed class NhAssistantChatSubjects(
    NhAssistantSubjectAccessService subjects,
    NhAssistantTenancy tenancy)
{
    /// <summary>
    /// The least role on the subject with which a person uses a chat about it: reading it, as the
    /// person's own rights decide what the chat's tools may do.
    /// </summary>
    public const NhAiSubjectRole MinimumRole = NhAiSubjectRole.Reader;

    /// <summary>
    /// The subject of <paramref name="conversation"/>, or <see langword="null"/> for a chat without one
    /// and for an agent's thread.
    /// </summary>
    public static NhAiSubjectRef? SubjectOf(AssistantConversation conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        if (conversation.SubjectType is not { } type || conversation.SubjectId is not { } id)
        {
            return null;
        }

        // A stored subject was valid when the chat was created; one that is not is corrupt state.
        return new NhAiSubjectRef(type, id);
    }

    /// <summary>
    /// The subject a request names with <paramref name="subjectType"/> and <paramref name="subjectId"/>:
    /// <see langword="null"/> when it names none, <see cref="NhAssistantErrorCodes.SubjectInvalid"/> when it
    /// names only one of them, an invalid type or id, or a type of the platform, such as the whole
    /// application, which a chat without a subject already is about.
    /// </summary>
    public static TaskResult<NhAiSubjectRef?> Parse(string? subjectType, string? subjectId)
    {
        if (subjectType is null && subjectId is null)
        {
            return TaskResult<NhAiSubjectRef?>.Succeeded(null);
        }

        if (!NhAiSubjectRef.IsValidType(subjectType)
            || NhAiSubjectRef.IsReservedType(subjectType)
            || string.IsNullOrWhiteSpace(subjectId)
            || subjectId.Length > NhAiSubjectRef.MaxIdLength
            || subjectId.Trim() != subjectId)
        {
            return TaskResult<NhAiSubjectRef?>.Failed(NhAssistantErrorCodes.SubjectInvalid, "The chat's subject is not valid.");
        }

        return TaskResult<NhAiSubjectRef?>.Succeeded(new NhAiSubjectRef(subjectType!, subjectId));
    }

    /// <summary>
    /// The subject of a new chat of <paramref name="actorId"/> with <paramref name="agent"/>, as its
    /// resolver describes it: <see cref="NhAssistantErrorCodes.SubjectTypeUnknown"/> for a type without a
    /// resolver, the refusals of <see cref="CheckAsync(NhAiSubjectRef, string, string?, CancellationToken)"/>,
    /// and <see cref="NhAssistantErrorCodes.AgentDoesNotWorkOn"/> when the agent does not work on it.
    /// </summary>
    public async Task<TaskResult<NhAiSubjectInfo>> CheckNewAsync(
        NhAiSubjectRef subject,
        NhAssistantAgent agent,
        string actorId,
        string? tenantId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(agent);
        if (!subjects.IsKnownType(subject.Type))
        {
            return TaskResult<NhAiSubjectInfo>.Failed(NhAssistantErrorCodes.SubjectTypeUnknown, "The chat's subject type is not registered.");
        }

        var checkedSubject = await CheckAsync(subject, actorId, tenantId, cancellationToken);
        if (!checkedSubject.Success)
        {
            return checkedSubject;
        }

        return WorksOn(agent, subject)
            ? checkedSubject
            : TaskResult<NhAiSubjectInfo>.Failed(NhAssistantErrorCodes.AgentDoesNotWorkOn, "The agent does not work on the chat's subject.");
    }

    /// <summary>
    /// The person's live access to the subject of <paramref name="conversation"/> for a turn, read or
    /// invitation: <see langword="null"/> for a chat without a subject, otherwise the subject as its
    /// resolver describes it now, or the refusal of
    /// <see cref="CheckAsync(NhAiSubjectRef, string, string?, CancellationToken)"/>.
    /// </summary>
    public async Task<TaskResult<NhAiSubjectInfo?>> CheckAsync(
        AssistantConversation conversation,
        string actorId,
        string? tenantId,
        CancellationToken cancellationToken)
    {
        if (SubjectOf(conversation) is not { } subject)
        {
            return TaskResult<NhAiSubjectInfo?>.Succeeded(null);
        }

        var checkedSubject = await CheckAsync(subject, actorId, tenantId, cancellationToken);
        return checkedSubject.Success
            ? TaskResult<NhAiSubjectInfo?>.Succeeded(checkedSubject.Data)
            : TaskResult<NhAiSubjectInfo?>.Failed((TaskResult)checkedSubject);
    }

    /// <summary>
    /// The access of <paramref name="actorId"/>, bound to <paramref name="tenantId"/>, to the subject:
    /// <see cref="NhAssistantErrorCodes.SubjectNotFound"/> when its resolver does not describe it or the
    /// tenant does not reach it, and <see cref="NhAssistantErrorCodes.SubjectAccessDenied"/> when the
    /// resolver gives the person less than <see cref="MinimumRole"/> now.
    /// </summary>
    public async Task<TaskResult<NhAiSubjectInfo>> CheckAsync(
        NhAiSubjectRef subject,
        string actorId,
        string? tenantId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);
        var info = await subjects.DescribeAsync(subject, cancellationToken);
        if (info is null || !tenancy.Reaches(info, tenantId))
        {
            return TaskResult<NhAiSubjectInfo>.Failed(NhAssistantErrorCodes.SubjectNotFound, "The chat's subject was not found.");
        }

        var access = await subjects.ResolveAsync(subject, actorId, cancellationToken);
        return access.Has(MinimumRole)
            ? TaskResult<NhAiSubjectInfo>.Succeeded(info)
            : TaskResult<NhAiSubjectInfo>.Failed(NhAssistantErrorCodes.SubjectAccessDenied, "The person may not use a chat about this subject.");
    }

    /// <summary>
    /// Whether the person may read the chat about <paramref name="conversation"/>'s subject now, for what
    /// reaches them without a request: live updates and notifications. A chat without a subject asks
    /// nothing here.
    /// </summary>
    public async ValueTask<bool> MayReadAsync(
        AssistantConversation conversation,
        string actorId,
        CancellationToken cancellationToken)
    {
        var checkedSubject = await CheckAsync(conversation, actorId, conversation.TenantId, cancellationToken);
        return checkedSubject.Success;
    }

    /// <summary>
    /// Whether <paramref name="agent"/> works on <paramref name="subject"/>, as its triggers, starts and
    /// consultations ask.
    /// </summary>
    public static bool WorksOn(NhAssistantAgent agent, NhAiSubjectRef subject)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(subject);
        return agent.WorksOn.Covers(subject);
    }

    /// <summary>
    /// Whether <paramref name="agent"/> is offered for a new chat with or without a subject: an agent that
    /// requires one (<c>RequireChatSubject()</c>) only with a subject it works on, every other agent offered
    /// in the chat always, and with a subject only when it works on it.
    /// </summary>
    public static bool IsOffered(NhAssistantAgent agent, NhAiSubjectRef? subject)
    {
        ArgumentNullException.ThrowIfNull(agent);
        if (!agent.OfferedInChat)
        {
            return false;
        }

        return subject is null
            ? !agent.RequiresChatSubject
            : WorksOn(agent, subject);
    }
}
