using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Collaboration;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet;

/// <summary>
/// Creates, lists, reads and stops a person's chat, lists the agents the person may chat with and
/// uploads a file to a chat, for the application's own channels, such as a messaging app in which a
/// person talks with the assistant; <see cref="INhAssistantConversationTurns"/> sends, decides and
/// answers in it. It is a server API for the application's code; there is no endpoint for it. The
/// application vouches for the person, as for <see cref="INhAssistantConversationTurns"/>: it
/// authenticated them through its channel and passes their principal, actor id and tenant, as the
/// assistant's context resolver gives them for the person's own requests.
/// <para>
/// Each call is the call of the endpoint (<c>POST conversations</c>, <c>GET conversations</c>,
/// <c>GET conversations/{id}</c>, <c>POST conversations/{id}/cancel</c>, <c>GET agents</c> and
/// <c>POST conversations/{id}/files</c>) with the same checks, codes and audit, which records none of
/// them. The interface grows with the endpoints, so implement or mock it only in the application's own
/// tests. It runs without a request of the person and without the caller's request, in services of its
/// own. The assistant's access policy, which every endpoint requires, is checked with the person's
/// resource and answers <c>assistant-agent-forbidden</c>. Only chats are served; an agent's thread
/// answers <c>assistant-conversation-not-found</c>. Every call first fails with
/// <c>assistant-disabled</c> when the assistant is off and <c>assistant-context-unavailable</c> when
/// the application's resolver refuses the principal or resolves another actor or tenant than the person
/// names.
/// </para>
/// </summary>
public interface INhAssistantConversations
{
    /// <summary>
    /// Starts an empty chat of <paramref name="person"/> with an agent, in the person's tenant, and returns
    /// it as <c>GET conversations/{id}</c> shows it. With <see cref="NhAssistantNewConversation.Subject"/>
    /// the chat is about that subject (FB25), as <c>POST conversations</c> with <c>subjectType</c> and
    /// <c>subjectId</c>: every later turn, read and invitation checks the person's role on it again.
    /// <para>
    /// Fails with <c>assistant-title-invalid</c> for a title longer than 200 characters,
    /// <c>assistant-agent-not-found</c> for an unknown agent, one that is off or one kept out of the chat,
    /// <c>assistant-agent-forbidden</c> when the person fails the assistant's access policy or the
    /// agent's policy, <c>assistant-subject-required</c> for an agent offered only for chats about a
    /// subject and a chat without one, <c>assistant-subject-invalid</c> for a subject of the platform,
    /// <c>assistant-subject-type-unknown</c>, <c>assistant-subject-not-found</c> when its resolver does not
    /// describe it in the person's tenant, <c>assistant-subject-access-denied</c> when it gives the person
    /// no role on it, and <c>assistant-agent-does-not-work-on</c> when the agent does not work on it.
    /// </para>
    /// </summary>
    /// <param name="person">The person the application authenticated.</param>
    /// <param name="conversation">The agent, the title and the subject.</param>
    /// <param name="options">The language and the person's active division; English without one.</param>
    Task<TaskResult<NhAssistantConversationDto>> CreateAsync(
        NhAssistantDecidingPerson person,
        NhAssistantNewConversation conversation,
        NhAssistantConversationOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the person's chat as <c>GET conversations/{id}</c> returns it: its messages, its members, the
    /// approval or question it waits for and, for its owner, the invitation link's token.
    /// <para>
    /// Fails with <c>assistant-conversation-not-found</c> for a chat the person does not take part in, of
    /// another tenant, or an agent's thread, and <c>assistant-agent-forbidden</c> when the person fails
    /// the assistant's access policy. A chat about a subject answers <c>assistant-subject-not-found</c> or
    /// <c>assistant-subject-access-denied</c>, also to its owner, once the subject's resolver no longer
    /// describes it in the person's tenant or gives the person a role on it. A participant must also
    /// satisfy the agent's policy (<c>assistant-agent-not-found</c> when the agent is gone); the owner
    /// reads their chat whatever became of its agent.
    /// </para>
    /// </summary>
    Task<TaskResult<NhAssistantConversationDto>> GetAsync(
        NhAssistantDecidingPerson person,
        Guid conversationId,
        NhAssistantConversationOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the person's running turn in this process, or rejects the approval and cancels the question
    /// the chat waits for, as <c>POST conversations/{id}/cancel</c> does; a chat without a running or
    /// waiting turn succeeds unchanged.
    /// <para>
    /// Fails with <c>assistant-conversation-not-found</c> as <see cref="GetAsync"/> does,
    /// <c>assistant-agent-forbidden</c> when the person fails the assistant's access policy, and
    /// <c>assistant-turn-forbidden</c> when the person neither owns the chat nor started its turn. The
    /// agent's policy and the role on a chat's subject are not checked, so a person whose permission was
    /// revoked can still stop a turn.
    /// </para>
    /// </summary>
    Task<TaskResult> CancelAsync(
        NhAssistantDecidingPerson person,
        Guid conversationId,
        NhAssistantConversationOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the chats the person owns or takes part in, most recently updated first, with the person's
    /// read state and the subject of a chat about one, as <c>GET conversations</c> returns them. Agents'
    /// threads are not listed; they live on their subject's page. The list asks no subject's resolver;
    /// opening a chat about a subject does.
    /// <para>
    /// Fails with <c>assistant-message-invalid</c> for a page below 1 or a page size outside 1 to 100,
    /// and <c>assistant-agent-forbidden</c> when the person fails the assistant's access policy.
    /// </para>
    /// </summary>
    /// <param name="page">The page and its size; the first 20 chats without one.</param>
    Task<TaskResult<NhAssistantConversationListDto>> ListAsync(
        NhAssistantDecidingPerson person,
        NhAssistantConversationPage? page = null,
        NhAssistantConversationOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the agents the person may chat with, as <c>GET agents</c> returns them: those offered in the
    /// chat whose policy the person satisfies, except those offered only for chats about a subject
    /// (<c>RequireChatSubject()</c>), each with what people may attach to a message to it
    /// (<see cref="NhAssistantAgentSummaryDto.Uploads"/>), which is <see langword="null"/> when the
    /// application takes no uploads, the agent accepts no files or its profile chain may not receive the
    /// uploads' classification.
    /// <para>
    /// Fails with <c>assistant-agent-forbidden</c> when the person fails the assistant's access policy.
    /// </para>
    /// </summary>
    Task<TaskResult<IReadOnlyList<NhAssistantAgentSummaryDto>>> ListAgentsAsync(
        NhAssistantDecidingPerson person,
        NhAssistantConversationOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the agents for a chat of the person about <paramref name="subject"/> (FB25), as
    /// <c>GET agents?subjectType=&amp;subjectId=</c> returns them: those offered in the chat whose policy the
    /// person satisfies and that work on the subject, those offered only for chats about a subject
    /// included.
    /// <para>
    /// Fails with <c>assistant-agent-forbidden</c> when the person fails the assistant's access policy,
    /// <c>assistant-subject-invalid</c> for a subject of the platform, <c>assistant-subject-type-unknown</c>,
    /// <c>assistant-subject-not-found</c> when its resolver does not describe it in the person's tenant and
    /// <c>assistant-subject-access-denied</c> when it gives the person no role on it.
    /// </para>
    /// </summary>
    Task<TaskResult<IReadOnlyList<NhAssistantAgentSummaryDto>>> ListAgentsAsync(
        NhAssistantDecidingPerson person,
        NhAiSubjectRef subject,
        NhAssistantConversationOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Uploads one file the person attaches to a later message in their chat, as
    /// <c>POST conversations/{id}/files</c> does, and returns the reference that
    /// <see cref="NhAssistantConversationMessage.Files"/> then carries. The application's upload scanner,
    /// when it registered one, scans the file before the file source stores it with the uploads'
    /// classification. The file's name is never logged.
    /// <para>
    /// Fails, in this order, with <c>assistant-conversation-not-found</c> as <see cref="GetAsync"/> does,
    /// <c>assistant-agent-forbidden</c> without the access policy, <c>assistant-subject-not-found</c> or
    /// <c>assistant-subject-access-denied</c> for a chat about a subject the person may no longer use,
    /// <c>assistant-agent-not-found</c> or
    /// <c>assistant-agent-forbidden</c> when the person may not use the agent now (the owner too, since
    /// uploading belongs to starting a turn), <c>assistant-files-unavailable</c> without a file source,
    /// <c>assistant-files-not-accepted</c> when the agent takes no files or its profile chain may not
    /// receive them, <c>assistant-file-upload-invalid</c> for a file without a usable name or content,
    /// <c>assistant-file-type-not-accepted</c>, <c>assistant-file-too-large</c>,
    /// <c>assistant-file-scan-refused</c>, <c>assistant-file-upload-refused</c> and
    /// <c>assistant-file-upload-failed</c>, with the endpoint's codes.
    /// </para>
    /// </summary>
    /// <param name="upload">The file: its name, its media type and its content, which the call reads
    /// more than once from its current position; the caller disposes it.</param>
    /// <exception cref="ArgumentException">The content cannot be read or cannot seek.</exception>
    Task<TaskResult<NhAiFileReference>> UploadAsync(
        NhAssistantDecidingPerson person,
        Guid conversationId,
        NhAssistantConversationUpload upload,
        NhAssistantConversationOptions? options = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A page of <see cref="INhAssistantConversations.ListAsync"/>, as <c>page</c> and <c>itemsPerPage</c>
/// of <c>GET conversations</c>.
/// </summary>
/// <param name="Page">The page, from 1.</param>
/// <param name="ItemsPerPage">The chats of a page, 1 to 100.</param>
public sealed record NhAssistantConversationPage(int Page = 1, int ItemsPerPage = 20);

/// <summary>
/// A file that <see cref="INhAssistantConversations.UploadAsync"/> uploads to a chat.
/// </summary>
/// <param name="FileName">The name the person's device gave the file; a path before it is dropped.</param>
/// <param name="MediaType">The media type the device gave it; <see langword="null"/> or a generic type
/// takes the accepted type of the name's extension, as for the endpoint.</param>
/// <param name="Content">The content from its current position to its end: a readable stream that can
/// seek, because the call reads it again for the content check, the scanner and the file source. The
/// caller keeps and disposes it.</param>
public sealed record NhAssistantConversationUpload(
    string FileName,
    string? MediaType,
    Stream Content);

/// <summary>
/// A chat that <see cref="INhAssistantConversations.CreateAsync"/> starts.
/// </summary>
/// <param name="AgentId">The agent the person chats with.</param>
public sealed record NhAssistantNewConversation(string AgentId)
{
    /// <summary>
    /// The chat's title, at most 200 characters; <see langword="null"/> lets the first message name it.
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// The subject the chat is about (FB25), such as the project a person asks about from its page;
    /// <see langword="null"/> for a chat without one. Its resolver must describe it in the person's tenant
    /// and give the person a role on it, and the agent must work on it. The chat offers it to tools
    /// (<see cref="NhAiInvocationContext.ConversationSubject"/>), the file source, proposal guards and the
    /// briefing, and stays the person's chat, apart from the agent's thread about the subject.
    /// </summary>
    public NhAiSubjectRef? Subject { get; init; }
}

/// <summary>
/// A new chat of a person, shared by <c>POST conversations</c> and
/// <see cref="INhAssistantConversations.CreateAsync"/>: the checks of its subject (FB25) and the row.
/// </summary>
internal static class NhAssistantNewChats
{
    public const int MaxTitleLength = 200;

    /// <summary>
    /// The subject of a new chat with <paramref name="agent"/> as its resolver describes it:
    /// <c>assistant-subject-required</c> without one for an agent offered only for chats about a subject,
    /// <see langword="null"/> without one otherwise, and the refusals of
    /// <see cref="NhAssistantChatSubjects.CheckNewAsync"/> with one.
    /// </summary>
    public static async Task<TaskResult<NhAiSubjectInfo?>> CheckSubjectAsync(
        IServiceProvider services,
        NhAssistantAgent agent,
        NhAiSubjectRef? subject,
        NhAiInvocationContext caller,
        CancellationToken cancellationToken)
    {
        if (subject is null)
        {
            return agent.RequiresChatSubject
                ? TaskResult<NhAiSubjectInfo?>.Failed(NhAssistantErrorCodes.SubjectRequired, "The agent is offered only for chats about a subject.")
                : TaskResult<NhAiSubjectInfo?>.Succeeded(null);
        }
        if (NhAiSubjectRef.IsReservedType(subject.Type))
        {
            return TaskResult<NhAiSubjectInfo?>.Failed(NhAssistantErrorCodes.SubjectInvalid, "The chat's subject is not valid.");
        }

        var checkedSubject = await services.GetRequiredService<NhAssistantChatSubjects>()
            .CheckNewAsync(subject, agent, caller.ActorId, caller.TenantId, cancellationToken);
        return checkedSubject.Success
            ? TaskResult<NhAiSubjectInfo?>.Succeeded(checkedSubject.Data)
            : TaskResult<NhAiSubjectInfo?>.Failed(checkedSubject);
    }

    /// <summary>
    /// The row of the new chat of <paramref name="caller"/>, about <paramref name="subject"/> when it has
    /// one, with the subject's name and scope as its resolver described them.
    /// </summary>
    public static AssistantConversation Create(
        NhAiInvocationContext caller,
        string ownerName,
        NhAssistantAgentDefinition agent,
        string? title,
        NhAiSubjectInfo? subject)
    {
        var now = DateTimeOffset.UtcNow;
        var displayName = subject?.DisplayName?.Trim();
        if (displayName is { Length: > NhAiSubjectParent.MaxDisplayNameLength })
        {
            displayName = displayName[..NhAiSubjectParent.MaxDisplayNameLength].TrimEnd();
        }

        return new AssistantConversation
        {
            Id = Guid.NewGuid(),
            OwnerActorId = caller.ActorId,
            OwnerDisplayName = ownerName.Length == 0 ? null : ownerName,
            TenantId = caller.TenantId,
            AgentId = agent.Id,
            AgentVersion = agent.Version,
            Title = string.IsNullOrWhiteSpace(title) ? null : title,
            SubjectType = subject?.Subject.Type,
            SubjectId = subject?.Subject.Id,
            SubjectDisplayName = string.IsNullOrEmpty(displayName) ? null : displayName,
            SubjectScopeKey = subject?.ScopeKey,
            Status = NhAssistantConversationStatuses.Idle,
            CreatedAt = now,
            UpdatedAt = now,
            ConcurrencyStamp = Guid.NewGuid()
        };
    }
}

/// <summary>
/// How a call of <see cref="INhAssistantConversations"/> runs.
/// </summary>
public sealed record NhAssistantConversationOptions
{
    /// <summary>
    /// The language of the call: <c>nl</c> for Dutch and <c>en</c> otherwise, as the endpoints read
    /// <c>Accept-Language</c>; the conversation's presentations follow it.
    /// </summary>
    public string Language { get; init; } = "en";

    /// <summary>
    /// The person's active division, as for <see cref="NhAssistantConversationTurnOptions.ActiveDivisionId"/>:
    /// it counts only when the host maps the active division and the person passes its policy, and a
    /// division access policy reads it. <see langword="null"/> without one.
    /// </summary>
    public Guid? ActiveDivisionId { get; init; }
}

/// <summary>
/// Creates, reads and stops a person's chat without the caller's request (FB5), and lists their chats
/// and agents and uploads to a chat with the endpoints' checks (FB23).
/// </summary>
internal sealed class NhAssistantConversations(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<NhAssistantOptions> options) : INhAssistantConversations
{
    private const int MaxTitleLength = NhAssistantNewChats.MaxTitleLength;

    public Task<TaskResult<NhAssistantConversationDto>> CreateAsync(
        NhAssistantDecidingPerson person,
        NhAssistantNewConversation conversation,
        NhAssistantConversationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        NhAssistantPersonCalls.ValidatePerson(person);
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentException.ThrowIfNullOrWhiteSpace(conversation.AgentId, nameof(conversation));

        return RunAsync<NhAssistantConversationDto>(
            person,
            options,
            async (services, personInvocation, token) =>
            {
                var title = conversation.Title?.Trim();
                if (title is { Length: > MaxTitleLength })
                {
                    return TaskResult<NhAssistantConversationDto>.Failed(NhAssistantErrorCodes.TitleInvalid, "The conversation title is too long.");
                }

                var effective = await services.GetRequiredService<NhAssistantAgentCatalog>()
                    .FindAsync(conversation.AgentId, includeDisabled: false, token);
                // An agent kept out of the chat reads as unknown here; its runs and subject threads go on.
                if (effective is null || !effective.OfferedInChat)
                {
                    return TaskResult<NhAssistantConversationDto>.Failed(NhAssistantErrorCodes.AgentNotFound, "The assistant agent was not found.");
                }

                var agent = effective.Definition;
                if (!await NhAssistantPersonCalls.PassesAccessPolicyAsync(services, person, personInvocation)
                    || !await services.GetRequiredService<NhAssistantAgentAccess>().CanUseAsync(person.User, agent))
                {
                    return TaskResult<NhAssistantConversationDto>.Failed(NhAssistantErrorCodes.AgentForbidden, "The person may not use the assistant agent.");
                }

                var caller = personInvocation.Context;
                var subject = await NhAssistantNewChats.CheckSubjectAsync(services, effective, conversation.Subject, caller, token);
                if (!subject.Success)
                {
                    return TaskResult<NhAssistantConversationDto>.Failed(subject);
                }

                var ownerName = NhAssistantParticipantNames.Normalize(
                    await services.GetRequiredService<INhAssistantDisplayNameResolver>().GetDisplayNameAsync(person.User, caller, token));
                var created = NhAssistantNewChats.Create(caller, ownerName, agent, title, subject.Data);
                await services.GetRequiredService<INhAssistantStore>().AddConversationAsync(created, token);
                var view = await services.GetRequiredService<NhAssistantConversationReader>().CreateViewAsync(created, token);
                return TaskResult<NhAssistantConversationDto>.Succeeded(NhAssistantDtoMapper.ToDto(view, caller.ActorId));
            },
            cancellationToken);
    }

    public Task<TaskResult<NhAssistantConversationDto>> GetAsync(
        NhAssistantDecidingPerson person,
        Guid conversationId,
        NhAssistantConversationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        NhAssistantPersonCalls.ValidatePerson(person);

        return RunAsync<NhAssistantConversationDto>(
            person,
            options,
            async (services, personInvocation, token) =>
            {
                var chat = await FindChatAsync(services, person, personInvocation, conversationId, token);
                if (!chat.Success)
                {
                    return TaskResult<NhAssistantConversationDto>.Failed(chat);
                }

                var access = chat.Data;
                if (!access.IsOwner)
                {
                    // A participant must satisfy the agent's policy to read, so a revoked permission
                    // takes effect at once; the owner reads their chat whatever became of its agent.
                    var effective = await services.GetRequiredService<NhAssistantAgentCatalog>()
                        .FindAsync(access.Conversation.AgentId, includeDisabled: false, token);
                    if (effective is null)
                    {
                        return TaskResult<NhAssistantConversationDto>.Failed(NhAssistantErrorCodes.AgentNotFound, "The assistant agent is no longer available.");
                    }
                    if (!await services.GetRequiredService<NhAssistantAgentAccess>().CanUseAsync(person.User, effective.Definition))
                    {
                        return TaskResult<NhAssistantConversationDto>.Failed(NhAssistantErrorCodes.AgentForbidden, "The person may not use the assistant agent.");
                    }
                }

                return TaskResult<NhAssistantConversationDto>.Succeeded(
                    await NhAssistantEndpointRouteBuilderExtensions.CreateConversationDtoAsync(
                        access,
                        services.GetRequiredService<NhAssistantConversationReader>(),
                        services.GetRequiredService<NhAssistantCollaboration>(),
                        token));
            },
            cancellationToken);
    }

    public async Task<TaskResult> CancelAsync(
        NhAssistantDecidingPerson person,
        Guid conversationId,
        NhAssistantConversationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        NhAssistantPersonCalls.ValidatePerson(person);

        var cancelled = await RunAsync<bool>(
            person,
            options,
            async (services, personInvocation, token) =>
            {
                var chat = await FindChatAsync(services, person, personInvocation, conversationId, token, subjectCheck: false);
                if (!chat.Success)
                {
                    return TaskResult<bool>.Failed(chat);
                }

                // Only the owner or the person who started the turn stops it, as for the endpoint.
                var result = await services.GetRequiredService<INhAssistantTurnRunner>().CancelAsync(chat.Data, token);
                return result.Success ? TaskResult<bool>.Succeeded(true) : TaskResult<bool>.Failed(result);
            },
            cancellationToken);
        return cancelled.Success ? TaskResult.Succeeded() : TaskResult.Failed(cancelled);
    }

    public Task<TaskResult<NhAssistantConversationListDto>> ListAsync(
        NhAssistantDecidingPerson person,
        NhAssistantConversationPage? page = null,
        NhAssistantConversationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        NhAssistantPersonCalls.ValidatePerson(person);
        var requested = page ?? new NhAssistantConversationPage();

        return RunAsync<NhAssistantConversationListDto>(
            person,
            options,
            async (services, personInvocation, token) =>
            {
                if (!await NhAssistantPersonCalls.PassesAccessPolicyAsync(services, person, personInvocation))
                {
                    return TaskResult<NhAssistantConversationListDto>.Failed(NhAssistantErrorCodes.AgentForbidden, "The person may not use the assistant.");
                }

                return await NhAssistantPersonLists.ListConversationsAsync(
                    services.GetRequiredService<INhAssistantStore>(),
                    services.GetRequiredService<NhAssistantSubjectAccessService>(),
                    personInvocation.Context,
                    requested.Page,
                    requested.ItemsPerPage,
                    token);
            },
            cancellationToken);
    }

    public Task<TaskResult<IReadOnlyList<NhAssistantAgentSummaryDto>>> ListAgentsAsync(
        NhAssistantDecidingPerson person,
        NhAssistantConversationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        NhAssistantPersonCalls.ValidatePerson(person);
        return ListAgentsForAsync(person, null, options, cancellationToken);
    }

    public Task<TaskResult<IReadOnlyList<NhAssistantAgentSummaryDto>>> ListAgentsAsync(
        NhAssistantDecidingPerson person,
        NhAiSubjectRef subject,
        NhAssistantConversationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        NhAssistantPersonCalls.ValidatePerson(person);
        ArgumentNullException.ThrowIfNull(subject);
        return ListAgentsForAsync(person, subject, options, cancellationToken);
    }

    /// <summary>
    /// The agents of <c>GET agents</c>, for a chat without a subject or about <paramref name="subject"/>.
    /// </summary>
    private Task<TaskResult<IReadOnlyList<NhAssistantAgentSummaryDto>>> ListAgentsForAsync(
        NhAssistantDecidingPerson person,
        NhAiSubjectRef? subject,
        NhAssistantConversationOptions? options,
        CancellationToken cancellationToken)
    {
        return RunAsync<IReadOnlyList<NhAssistantAgentSummaryDto>>(
            person,
            options,
            async (services, personInvocation, token) =>
            {
                if (!await NhAssistantPersonCalls.PassesAccessPolicyAsync(services, person, personInvocation))
                {
                    return TaskResult<IReadOnlyList<NhAssistantAgentSummaryDto>>.Failed(NhAssistantErrorCodes.AgentForbidden, "The person may not use the assistant.");
                }

                if (subject is not null)
                {
                    var checkedSubject = await NhAssistantPersonLists.CheckAgentsSubjectAsync(
                        services.GetRequiredService<NhAssistantSubjectAccessService>(),
                        services.GetRequiredService<NhAssistantChatSubjects>(),
                        personInvocation.Context,
                        subject,
                        token);
                    if (!checkedSubject.Success)
                    {
                        return TaskResult<IReadOnlyList<NhAssistantAgentSummaryDto>>.Failed(checkedSubject);
                    }
                }

                var agents = await NhAssistantPersonLists.ListAgentsAsync(
                    services.GetRequiredService<NhAssistantAgentAccess>(),
                    services.GetRequiredService<NhAssistantUploads>(),
                    person.User,
                    token,
                    subject);
                return TaskResult<IReadOnlyList<NhAssistantAgentSummaryDto>>.Succeeded(agents);
            },
            cancellationToken);
    }

    public Task<TaskResult<NhAiFileReference>> UploadAsync(
        NhAssistantDecidingPerson person,
        Guid conversationId,
        NhAssistantConversationUpload upload,
        NhAssistantConversationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        NhAssistantPersonCalls.ValidatePerson(person);
        ArgumentNullException.ThrowIfNull(upload);
        // Programmer input: a stream the checks cannot read again is refused before anything runs.
        var input = NhAssistantUploadInput.FromStream(upload.FileName, upload.MediaType, upload.Content);

        return RunAsync<NhAiFileReference>(
            person,
            options,
            async (services, personInvocation, token) =>
            {
                var chat = await FindChatAsync(services, person, personInvocation, conversationId, token);
                if (!chat.Success)
                {
                    return TaskResult<NhAiFileReference>.Failed(chat);
                }

                // The checks of the upload endpoint, from the agent's policy to the file source.
                var uploads = services.GetRequiredService<NhAssistantConversationUploads>();
                var prepared = await uploads.PrepareAsync(person.User, chat.Data!, thread: null, token);
                if (!prepared.Success)
                {
                    return TaskResult<NhAiFileReference>.Failed(prepared);
                }

                return await uploads.StoreAsync(prepared.Data!, input, token);
            },
            cancellationToken);
    }

    /// <summary>
    /// The person's chat, after the assistant's access policy, which every endpoint requires, and, unless
    /// <paramref name="subjectCheck"/> is <see langword="false"/>, the person's live role on the subject
    /// of a chat about one (FB25), as the endpoints check it.
    /// </summary>
    private static async Task<TaskResult<NhAssistantConversationAccess>> FindChatAsync(
        IServiceProvider services,
        NhAssistantDecidingPerson person,
        NhAiPersonInvocation personInvocation,
        Guid conversationId,
        CancellationToken cancellationToken,
        bool subjectCheck = true)
    {
        var chat = await NhAssistantPersonCalls.FindChatAsync(services, personInvocation.Context, conversationId, cancellationToken);
        if (!chat.Success)
        {
            return chat;
        }

        if (!await NhAssistantPersonCalls.PassesAccessPolicyAsync(services, person, personInvocation))
        {
            return TaskResult<NhAssistantConversationAccess>.Failed(NhAssistantErrorCodes.AgentForbidden, "The person may not use the assistant.");
        }
        if (!subjectCheck)
        {
            return chat;
        }

        var caller = personInvocation.Context;
        var checkedSubject = await services.GetRequiredService<NhAssistantChatSubjects>()
            .CheckAsync(chat.Data!.Conversation, caller.ActorId, caller.TenantId, cancellationToken);
        return checkedSubject.Success
            ? chat
            : TaskResult<NhAssistantConversationAccess>.Failed(checkedSubject);
    }

    /// <summary>
    /// Runs the call for the resolved person, without the caller's request and in services of its own.
    /// </summary>
    private async Task<TaskResult<T>> RunAsync<T>(
        NhAssistantDecidingPerson person,
        NhAssistantConversationOptions? callOptions,
        Func<IServiceProvider, NhAiPersonInvocation, CancellationToken, Task<TaskResult<T>>> call,
        CancellationToken cancellationToken)
    {
        if (!options.CurrentValue.Enabled)
        {
            return TaskResult<T>.Failed(NhAssistantErrorCodes.Disabled, "The assistant is disabled.");
        }

        var resolvedOptions = callOptions ?? new NhAssistantConversationOptions();
        return await NhAssistantPersonCalls.RunWithoutCallerAsync(
            scopeFactory,
            resolvedOptions.Language,
            async (services, _) =>
            {
                var resolved = await NhAssistantPersonCalls.ResolveAsync(services, person, resolvedOptions.ActiveDivisionId, cancellationToken);
                if (!resolved.Success)
                {
                    return TaskResult<T>.Failed(resolved);
                }

                return await call(services, resolved.Data, cancellationToken);
            },
            cancellationToken);
    }
}
