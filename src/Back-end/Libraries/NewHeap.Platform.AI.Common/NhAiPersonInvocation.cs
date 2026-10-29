using System.Security.Claims;

namespace NewHeap.Platform.AI;

/// <summary>
/// A person's turn that does not depend on the person's own request: a turn that outlives the request
/// that started it, or one the application started for a person it authenticated through its own
/// channel. It carries the person's principal and authorized invocation context as they were captured
/// when the turn started, and the resource the person's tool calls are authorized against. While it is
/// ambient, the request gate authorizes tool calls against it instead of the current request, so a tool
/// never authorizes a request that ended or that belongs to someone else.
/// </summary>
/// <param name="User">The person's principal: the request's user, or the principal the application
/// vouched for.</param>
/// <param name="Context">The person's authorized invocation context, resolved once when the turn
/// started.</param>
/// <param name="AuthorizationResource">The resource every tool policy of the person is evaluated with,
/// as the request gate would build it for the person's own request; <see langword="null"/> when that
/// request would have none.</param>
internal sealed record NhAiPersonInvocation(
    ClaimsPrincipal User,
    NhAiInvocationContext Context,
    object? AuthorizationResource)
{
    /// <summary>
    /// Every tool effect other than a read needs an approval in this person's turn, whatever the effect
    /// policy decides, as in an agent scope that requires approval for effects; tools that issue or
    /// authorize approvals themselves are denied. Set for a turn that nobody typed, such as the turn an
    /// application's execution report starts in the person's chat, so it can only propose.
    /// </summary>
    public bool RequireApprovalForEffects { get; init; }
}

/// <summary>
/// The ambient <see cref="NhAiPersonInvocation"/> of the current asynchronous flow.
/// </summary>
internal static class NhAiPersonInvocationScopes
{
    private static readonly AsyncLocal<NhAiPersonInvocation?> CurrentPerson = new();

    public static NhAiPersonInvocation? Current => CurrentPerson.Value;

    /// <summary>
    /// Makes <paramref name="person"/> current until the returned handle is disposed.
    /// </summary>
    public static IDisposable Begin(NhAiPersonInvocation person)
    {
        ArgumentNullException.ThrowIfNull(person);
        var previous = CurrentPerson.Value;
        CurrentPerson.Value = person;
        return new Restore(previous);
    }

    private sealed class Restore(NhAiPersonInvocation? previous) : IDisposable
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
