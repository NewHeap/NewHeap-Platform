using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.Collaboration;
using NewHeap.Platform.AspNet.Common.Services;

namespace NewHeap.Platform.AI.Chat.AspNet.Notifications;

/// <summary>
/// The application's principal factory as far as it tells something about a person: the factory a
/// headless run rebuilds people with.
/// </summary>
internal static class NhAssistantPlatformPrincipals
{
    /// <summary>
    /// The factory when it says whether the platform still resolves a person, so that no principal means
    /// an unknown person or one who may no longer sign in: not the built-in factory in a host without the
    /// NewHeap user manager, which resolves nobody, and not the access source's factory
    /// (<c>UsePrincipalsFromAccessSource</c>), which says only whether the application holds a fresh
    /// context of the person.
    /// </summary>
    public static INhAiPrincipalFactory? Resolving(IServiceProvider services)
    {
        var principals = Rebuilding(services);
        return principals is NhAiAccessSourcePrincipalFactory ? null : principals;
    }

    /// <summary>
    /// The factory when it can rebuild anyone at all: every factory except the built-in one in a host
    /// without the NewHeap user manager.
    /// </summary>
    public static INhAiPrincipalFactory? Rebuilding(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var principals = services.GetService<INhAiPrincipalFactory>();
        if (principals is null)
        {
            return null;
        }

        var builtIn = principals.GetType().Assembly == typeof(INhAiPrincipalFactory).Assembly
            && principals is not NhAiAccessSourcePrincipalFactory;
        return builtIn && services.GetService<INhUserManager>() is null ? null : principals;
    }
}

/// <summary>
/// Answers the recipient rules of deliveries without a request of the person with the application's
/// principal factory, as headless runs rebuild people (<see cref="NhAssistantPlatformPrincipals"/>). A
/// rebuilt person must pass the policy. A factory that resolves people and rebuilds nobody means the
/// person is unknown or may no longer sign in, such as a deleted, blocked or locked-out account, so they
/// pass nothing and are not resolved. A host whose factory cannot tell, or an access source without a
/// fresh context of the person, keeps delivering as before. A failing lookup passes nothing for that
/// person and is logged.
/// </summary>
internal sealed partial class NhAssistantPrincipalRecipientRules(
    IServiceProvider services,
    IAuthorizationService authorization,
    ILogger<NhAssistantPrincipalRecipientRules> logger) : INhAssistantRecipientRules
{
    /// <summary>The subject a person is resolved for, as the MCP owner sweep resolves them.</summary>
    private static readonly NhAiSubjectInfo WholeApplication = new(NhAiSystemSubjects.Global, NhAiSubjectInfo.CreateScopeKey(null, null));

    public async ValueTask<bool> PassesPolicyAsync(
        string actorId,
        NhAiSubjectInfo subject,
        string policy,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(policy);
        var principals = NhAssistantPlatformPrincipals.Rebuilding(services);
        if (principals is null)
        {
            return true;
        }

        try
        {
            var principal = await principals.CreateAsync(actorId, subject, cancellationToken);
            if (principal is null)
            {
                return principals is NhAiAccessSourcePrincipalFactory;
            }

            return (await authorization.AuthorizeAsync(principal, policy)).Succeeded;
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            LogLookupFailed(logger, exception);
            return false;
        }
    }

    /// <summary>
    /// Whether the factory that resolves people (<see cref="NhAssistantPlatformPrincipals.Resolving"/>)
    /// rebuilds the person for the whole application, the question <c>NhAssistantMcpOwnerSweep</c>
    /// asks: an unknown person, or one who may no longer sign in, is not resolved. Without such a
    /// factory everyone is.
    /// </summary>
    public async ValueTask<bool> IsResolvedAsync(string actorId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        var principals = NhAssistantPlatformPrincipals.Resolving(services);
        if (principals is null)
        {
            return true;
        }

        try
        {
            return await principals.CreateAsync(actorId, WholeApplication, cancellationToken) is not null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            LogLookupFailed(logger, exception);
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The assistant could not rebuild a recipient; nothing is delivered to that person this time.")]
    private static partial void LogLookupFailed(ILogger logger, Exception exception);
}
