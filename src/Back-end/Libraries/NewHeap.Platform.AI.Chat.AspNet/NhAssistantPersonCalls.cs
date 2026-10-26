using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet;

/// <summary>
/// What the services for the application's own channels (<see cref="INhAssistantConversationTurns"/>,
/// <see cref="INhAssistantConversations"/>) share: a call runs without the caller's request, in services
/// of its own and in the culture of its language; the person the application vouches for is resolved
/// with the application's resolver and must be the person that resolver names; and the person's chat
/// is found with the endpoints' rules (LS19).
/// </summary>
internal static class NhAssistantPersonCalls
{
    /// <summary>
    /// Runs <paramref name="call"/> in a flow of its own: without the caller's request, so nothing in it
    /// reads the caller's request or its principal, and in services of its own that end with the call.
    /// The caller waits for the result until its token fires.
    /// </summary>
    public static async Task<T> RunWithoutCallerAsync<T>(
        IServiceScopeFactory scopeFactory,
        string? language,
        Func<IServiceProvider, string, Task<T>> call,
        CancellationToken cancellationToken)
    {
        Task<T> running;
        using (ExecutionContext.SuppressFlow())
        {
            running = Task.Run(async () =>
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var services = scope.ServiceProvider;
                var turnLanguage = LanguageOf(language);
                UseCulture(services, turnLanguage);
                return await call(services, turnLanguage);
            });
        }

        return await running.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Resolves the person's invocation context with the application's resolver, with their active
    /// division. The application names the person; their own principal must say the same, or the call
    /// would act in one person's conversation with another person's rights:
    /// <c>assistant-context-unavailable</c> otherwise.
    /// </summary>
    public static async Task<TaskResult<NhAiPersonInvocation>> ResolveAsync(
        IServiceProvider services,
        NhAssistantDecidingPerson person,
        Guid? activeDivisionId,
        CancellationToken cancellationToken)
    {
        var resolved = await NhAiPersonInvocations.ResolveAsync(services, person.User, activeDivisionId, cancellationToken);
        if (!resolved.Success
            || !string.Equals(resolved.Data.Context.ActorId, person.ActorId, StringComparison.Ordinal)
            || !string.Equals(resolved.Data.Context.TenantId, person.TenantId, StringComparison.Ordinal))
        {
            return TaskResult<NhAiPersonInvocation>.Failed(
                NhAssistantErrorCodes.ContextUnavailable,
                "The person's invocation context is not available.");
        }

        return resolved;
    }

    /// <summary>
    /// The person's chat with the store's access rules: the owner whatever the tenant, a participant
    /// only in their tenant. An agent's thread follows its subject's access and runs; these services
    /// serve chats only, so it answers <c>assistant-conversation-not-found</c>, as an unknown chat does.
    /// </summary>
    public static async Task<TaskResult<NhAssistantConversationAccess>> FindChatAsync(
        IServiceProvider services,
        NhAiInvocationContext caller,
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        if (await services.GetRequiredService<NhAssistantRunStore>().FindThreadByConversationAsync(conversationId, cancellationToken) is not null)
        {
            return ConversationNotFound();
        }

        var access = await services.GetRequiredService<INhAssistantStore>().FindAccessAsync(
            conversationId,
            caller.ActorId,
            caller.TenantId,
            cancellationToken);
        return access is null
            ? ConversationNotFound()
            : TaskResult<NhAssistantConversationAccess>.Succeeded(access);
    }

    /// <summary>
    /// Whether the person passes the assistant's access policy, which every endpoint requires, evaluated
    /// with the resource of the person's turn, so a division access policy works without a request.
    /// </summary>
    public static async Task<bool> PassesAccessPolicyAsync(
        IServiceProvider services,
        NhAssistantDecidingPerson person,
        NhAiPersonInvocation personInvocation)
    {
        var accessPolicy = NhAssistantEndpointOptions.ResolveAccessPolicy(
            services.GetRequiredService<NhAssistantRegistrationState>(),
            services.GetRequiredService<IOptionsMonitor<NhAssistantOptions>>().CurrentValue);
        var authorization = await services.GetRequiredService<IAuthorizationService>()
            .AuthorizeAsync(person.User, personInvocation.AuthorizationResource, accessPolicy);
        return authorization.Succeeded;
    }

    public static void ValidatePerson(NhAssistantDecidingPerson person)
    {
        ArgumentNullException.ThrowIfNull(person);
        ArgumentNullException.ThrowIfNull(person.User, nameof(person));
        ArgumentException.ThrowIfNullOrWhiteSpace(person.ActorId, nameof(person));
    }

    /// <summary>
    /// <c>nl</c> for Dutch and <c>en</c> otherwise, as the endpoints read <c>Accept-Language</c>.
    /// </summary>
    public static string LanguageOf(string? language)
    {
        return language is not null && language.StartsWith("nl", StringComparison.OrdinalIgnoreCase) ? "nl" : "en";
    }

    /// <summary>
    /// Runs the call in the application's supported culture of its language, as the endpoints run a
    /// request that names only a language.
    /// </summary>
    private static void UseCulture(IServiceProvider services, string language)
    {
        var supported = services.GetService<IOptions<RequestLocalizationOptions>>()?.Value.SupportedUICultures ?? [];
        var culture = supported.FirstOrDefault(item =>
            string.Equals(item.TwoLetterISOLanguageName, language, StringComparison.OrdinalIgnoreCase));
        if (culture is null)
        {
            return;
        }

        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    private static TaskResult<NhAssistantConversationAccess> ConversationNotFound()
    {
        return TaskResult<NhAssistantConversationAccess>.Failed(
            NhAssistantErrorCodes.ConversationNotFound,
            "The assistant conversation was not found.");
    }
}
