using System.Net.Sockets;

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
}