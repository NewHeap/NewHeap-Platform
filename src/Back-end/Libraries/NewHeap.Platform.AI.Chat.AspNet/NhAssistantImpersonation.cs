using System.Security.Claims;
using NewHeap.Platform.Common.Identity.Claims;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet;

/// <summary>
/// A NewHeap impersonation session: an access token that acts as another person and names the user who
/// started it in <see cref="NhPlatformClaimTypes.ImpersonateOriginUserId"/>. The session acts as the
/// person for as long as it lasts, but grants nothing in the person's name that outlives it: consent to
/// background use of an MCP server, a personal MCP connection and a browser's push subscription are
/// refused with <see cref="NhAssistantErrorCodes.NotAllowedWhileImpersonating"/>, as the platform refuses
/// changes of the person's sign-in factors. Withdrawing them stays allowed, and the business audit names
/// the user who impersonated (<see cref="NhAssistantAuditEvent.ImpersonationOriginActorId"/>).
/// </summary>
internal static class NhAssistantImpersonation
{
    /// <summary>
    /// Whether <paramref name="user"/> belongs to an impersonation session. The marker alone decides, also
    /// when its value is empty.
    /// </summary>
    public static bool IsImpersonating(ClaimsPrincipal? user)
    {
        return user?.HasClaim(claim => claim.Type == NhPlatformClaimTypes.ImpersonateOriginUserId) == true;
    }

    /// <summary>
    /// The actor id of the user who impersonates the person in <paramref name="user"/>'s session, or
    /// <see langword="null"/> outside an impersonation session.
    /// </summary>
    public static string? OriginOf(ClaimsPrincipal? user)
    {
        var value = user?.FindFirst(NhPlatformClaimTypes.ImpersonateOriginUserId)?.Value;
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>The refusal of a grant in an impersonation session.</summary>
    public static TaskResult<T> Refused<T>()
    {
        return TaskResult<T>.Failed(
            NhAssistantErrorCodes.NotAllowedWhileImpersonating,
            "An impersonation session grants nothing in the person's name that outlives it.");
    }
}
