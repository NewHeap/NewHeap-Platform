using System.Security.Claims;

namespace NewHeap.Platform.AI;

/// <summary>
/// The person whose assistant turn the current flow works for, so a tool, an authorization policy or a
/// turn context provider can tell whom it works for without reading the request: in a turn of the
/// assistant's endpoints the request's signed-in user, and in a turn that the application starts for a
/// person through <c>INhAssistantConversationTurns</c>, or that outlives its request, the person captured
/// when it started. A consultation that such a turn starts works for the same person. Outside a person's
/// turn, such as in an agent run, a background job or a request that runs no turn, every member is
/// <see langword="null"/>.
/// <para>
/// The AI host registers it as a singleton (<c>AddNewHeapPlatformAIAspNet</c>), so services of any
/// lifetime inject it and read it when they need the person.
/// </para>
/// </summary>
public interface INhAiTurnPerson
{
    /// <summary>
    /// The person's principal: the request's user, or the principal the application vouched for;
    /// <see langword="null"/> outside a person's turn.
    /// </summary>
    ClaimsPrincipal? User { get; }

    /// <summary>
    /// The person's actor id, as the turn's invocation context resolved it; <see langword="null"/>
    /// outside a person's turn.
    /// </summary>
    string? ActorId { get; }

    /// <summary>
    /// The person's tenant, as the turn's invocation context resolved it; <see langword="null"/> outside
    /// a person's turn and for a person without a tenant.
    /// </summary>
    string? TenantId { get; }
}

/// <summary>
/// The person of the turn that runs in the current asynchronous flow, as the assistant's turn runner
/// marks it for the whole turn.
/// </summary>
/// <param name="ActorId">The person's actor id.</param>
/// <param name="TenantId">The person's tenant.</param>
/// <param name="User">The principal captured when the turn started, for a turn that does not depend on
/// the person's request; <see langword="null"/> for a turn that lives within its request, whose user is
/// the request's.</param>
internal sealed record NhAiTurnPersonMarker(string ActorId, string? TenantId, ClaimsPrincipal? User);

/// <summary>
/// The ambient <see cref="NhAiTurnPersonMarker"/> of the current asynchronous flow.
/// </summary>
internal static class NhAiTurnPersonScopes
{
    private static readonly AsyncLocal<NhAiTurnPersonMarker?> CurrentPerson = new();

    public static NhAiTurnPersonMarker? Current => CurrentPerson.Value;

    /// <summary>
    /// Marks <paramref name="person"/> as the turn's person until the returned handle is disposed.
    /// </summary>
    public static IDisposable Begin(NhAiTurnPersonMarker person)
    {
        ArgumentNullException.ThrowIfNull(person);
        var previous = CurrentPerson.Value;
        CurrentPerson.Value = person;
        return new Restore(previous);
    }

    private sealed class Restore(NhAiTurnPersonMarker? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CurrentPerson.Value = previous;
        }
    }
}
