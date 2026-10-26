using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace NewHeap.Platform.AI.AspNet;

/// <summary>
/// Reads the person of the current turn: the principal the turn captured when it does not depend on
/// the person's request, otherwise the user of the request the turn runs in. Nothing outside a turn
/// that the assistant marked as a person's.
/// </summary>
internal sealed class NhAiAspNetTurnPerson(IHttpContextAccessor httpContextAccessor) : INhAiTurnPerson
{
    public ClaimsPrincipal? User
    {
        get
        {
            if (NhAiTurnPersonScopes.Current is not { } person)
            {
                return null;
            }

            return person.User ?? httpContextAccessor.HttpContext?.User;
        }
    }

    public string? ActorId => NhAiTurnPersonScopes.Current?.ActorId;

    public string? TenantId => NhAiTurnPersonScopes.Current?.TenantId;
}
