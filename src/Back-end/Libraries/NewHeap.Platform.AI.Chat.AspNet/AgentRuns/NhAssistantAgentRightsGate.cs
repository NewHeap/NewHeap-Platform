using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// Decides who changes an agent's rights under <see cref="NhAssistantBuilder.UseAgentRightsPolicy"/>. The services
/// that save an agent authorize nothing themselves, so this follows the rule of the technical account's binding:
/// a person must pass the policy, and an actor the platform knows as no person is application code, whose change
/// is the application's own decision.
/// </summary>
internal sealed class NhAssistantAgentRightsGate(
    NhAssistantRegistrationState registration,
    IServiceProvider services,
    IAuthorizationService authorization,
    ILogger<NhAssistantAgentRightsGate> logger) : INhAssistantAgentRightsGate
{
    /// <summary>
    /// The subject a person is rebuilt for: the policy is asked without a resource, as the administration policy
    /// is, so it holds for the administration as a whole and not for the agent or server a change is about.
    /// </summary>
    private const string AdministrationSubject = "assistant-administration";

    public bool Enabled => registration.AgentRightsPolicy is not null;

    public async ValueTask<TaskResult?> CheckAsync(
        string objectId,
        IReadOnlyCollection<string> changes,
        string actorId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        if (!Enabled || changes.Count == 0)
        {
            return null;
        }

        var person = await NhAssistantCallerPrincipal.ResolveAsync(services, AdministrationSubject, actorId, cancellationToken);
        if (person is null || await PassesAsync(person))
        {
            return null;
        }

        logger.LogInformation(
            "A change of {ObjectId} was refused: the person does not pass the agent rights policy for {Changes}.",
            objectId,
            string.Join(", ", changes));
        return NhAssistantAgentRights.Refused(changes);
    }

    /// <summary>
    /// Whether <paramref name="person"/> may change an agent's rights, so an administration can say so beforehand.
    /// </summary>
    public async ValueTask<bool> MayChangeAsync(ClaimsPrincipal person)
    {
        ArgumentNullException.ThrowIfNull(person);
        return !Enabled || await PassesAsync(person);
    }

    /// <summary>
    /// Whether the person in <paramref name="actorId"/> may change an agent's rights, for a call that is known to
    /// work for a person, such as a chat with the agent builder: a person the platform cannot rebuild does not.
    /// </summary>
    public async ValueTask<bool> PersonMayChangeAsync(string actorId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        if (!Enabled)
        {
            return true;
        }

        var person = await NhAssistantCallerPrincipal.ResolveAsync(services, AdministrationSubject, actorId, cancellationToken);
        return person is not null && await PassesAsync(person);
    }

    private async Task<bool> PassesAsync(ClaimsPrincipal person)
    {
        return (await authorization.AuthorizeAsync(person, null, registration.AgentRightsPolicy!)).Succeeded;
    }
}

/// <summary>
/// The principal of the actor a service works for: the request's user when that actor sent the request, otherwise
/// the principal the factory rebuilds; <see langword="null"/> for an actor the platform knows as no person.
/// </summary>
internal static class NhAssistantCallerPrincipal
{
    public static async Task<ClaimsPrincipal?> ResolveAsync(
        IServiceProvider services,
        string agentId,
        string actorId,
        CancellationToken cancellationToken)
    {
        if (services.GetService<IHttpContextAccessor>()?.HttpContext is { } httpContext
            && await NhAiRequestActor.IsAsync(httpContext, actorId, services, cancellationToken))
        {
            return httpContext.User;
        }

        if (services.GetService<INhAiPrincipalFactory>() is not { } principals)
        {
            return null;
        }

        var subject = new NhAiSubjectInfo(new NhAiSubjectRef("agent", agentId), NhAiSubjectInfo.CreateScopeKey(null, null));
        return await principals.CreateAsync(actorId, subject, cancellationToken);
    }
}
