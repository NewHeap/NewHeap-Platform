using System.Net.Sockets;
using System.Security.Authentication;

namespace NewHeap.Platform.DatabaseRead;

internal static class DatabaseReadExceptionInspector
{
    public static TException? Find<TException>(Exception exception)
        where TException : Exception
    {
        ArgumentNullException.ThrowIfNull(exception);

        var pending = new Stack<Exception>();
        var visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        pending.Push(exception);

        while (pending.TryPop(out var current))
        {
            if (!visited.Add(current))
            {
                continue;
            }

            if (current is TException match)
            {
                return match;
            }

            if (current is AggregateException aggregateException)
            {
                foreach (var innerException in aggregateException.InnerExceptions)
                {
                    pending.Push(innerException);
                }
            }
            else if (current.InnerException is not null)
            {
                pending.Push(current.InnerException);
            }
        }

        return null;
    }

    public static bool IsConnectivityFailure(Exception exception)
    {
        return Find<SocketException>(exception) is not null;
    }

    /// <summary>
    /// Whether the TLS handshake failed because the client rejected the server's certificate.
    /// .NET reports a certificate that failed validation as an <see cref="AuthenticationException"/>
    /// without an inner exception; a handshake that failed for another reason, such as no common
    /// protocol version, carries the platform's error as its inner exception.
    /// </summary>
    public static bool IsCertificateValidationFailure(Exception exception)
    {
        var authenticationException = Find<AuthenticationException>(exception);

        return authenticationException is { InnerException: null };
    }
}