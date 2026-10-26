using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Collaboration;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet;

/// <summary>
/// Creates, reads and stops a person's chat for the application's own channels, such as a messaging
/// app in which a person talks with the assistant; <see cref="INhAssistantConversationTurns"/> sends,
/// decides and answers in it. It is a server API for the application's code; there is no endpoint for
/// it. The application vouches for the person, as for <see cref="INhAssistantConversationTurns"/>: it
/// authenticated them through its channel and passes their principal, actor id and tenant, as the
/// assistant's context resolver gives them for the person's own requests.
/// <para>
/// Each call is the call of the endpoint (<c>POST conversations</c>, <c>GET conversations/{id}</c> and
/// <c>POST conversations/{id}/cancel</c>) with the same checks, codes and audit, which records none of
/// them. It runs without a request of the person and without the caller's request, in services of its
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
    /// it as <c>GET conversations/{id}</c> shows it.
    /// <para>
    /// Fails with <c>assistant-title-invalid</c> for a title longer than 200 characters,
    /// <c>assistant-agent-not-found</c> for an unknown agent, one that is off or one kept out of the chat,
    /// and <c>assistant-agent-forbidden</c> when the person fails the assistant's access policy or the
    /// agent's policy.
    /// </para>
    /// </summary>
    /// <param name="person">The person the application authenticated.</param>
    /// <param name="conversation">The agent and the title.</param>
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
    /// the assistant's access policy. A participant must also satisfy the agent's policy
    /// (<c>assistant-agent-not-found</c> when the agent is gone); the owner reads their chat whatever
    /// became of its agent.
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
    /// agent's policy is not checked, so a person whose permission was revoked can still stop a turn.
    /// </para>
    /// </summary>
    Task<TaskResult> CancelAsync(
        NhAssistantDecidingPerson person,
        Guid conversationId,
        NhAssistantConversationOptions? options = null,
        CancellationToken cancellationToken = default);
}

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
/// Creates, reads and stops a person's chat without the caller's request (FB5).
/// </summary>
internal sealed class NhAssistantConversations(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<NhAssistantOptions> options) : INhAssistantConversations
{
    private const int MaxTitleLength = 200;

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
                var ownerName = NhAssistantParticipantNames.Normalize(
                    await services.GetRequiredService<INhAssistantDisplayNameResolver>().GetDisplayNameAsync(person.User, caller, token));
                var now = DateTimeOffset.UtcNow;
                var created = new AssistantConversation
                {
                    Id = Guid.NewGuid(),
                    OwnerActorId = caller.ActorId,
                    OwnerDisplayName = ownerName.Length == 0 ? null : ownerName,
                    TenantId = caller.TenantId,
                    AgentId = agent.Id,
                    AgentVersion = agent.Version,
                    Title = string.IsNullOrWhiteSpace(title) ? null : title,
                    Status = NhAssistantConversationStatuses.Idle,
                    CreatedAt = now,
                    UpdatedAt = now,
                    ConcurrencyStamp = Guid.NewGuid()
                };
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
                var chat = await FindChatAsync(services, person, personInvocation, conversationId, token);
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

    /// <summary>
    /// The person's chat, after the assistant's access policy, which every endpoint requires.
    /// </summary>
    private static async Task<TaskResult<NhAssistantConversationAccess>> FindChatAsync(
        IServiceProvider services,
        NhAssistantDecidingPerson person,
        NhAiPersonInvocation personInvocation,
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        var chat = await NhAssistantPersonCalls.FindChatAsync(services, personInvocation.Context, conversationId, cancellationToken);
        if (!chat.Success)
        {
            return chat;
        }

        return await NhAssistantPersonCalls.PassesAccessPolicyAsync(services, person, personInvocation)
            ? chat
            : TaskResult<NhAssistantConversationAccess>.Failed(NhAssistantErrorCodes.AgentForbidden, "The person may not use the assistant.");
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
