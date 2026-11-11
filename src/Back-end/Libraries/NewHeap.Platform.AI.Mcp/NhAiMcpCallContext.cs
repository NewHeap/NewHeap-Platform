using System.Collections.Concurrent;

namespace NewHeap.Platform.AI.Mcp;

/// <summary>
/// The governed call of an imported MCP tool that is in progress: the tool and the authorized
/// invocation context, after every invoker check and the invocation binder. It is set only while
/// <c>McpClientTool.CallAsync</c> runs, so an HTTP handler of the client's transport can decide
/// which credential a request carries, or refuse it, for the person or agent the call was
/// authorized for. It holds no credential and no argument values.
/// </summary>
public sealed class NhAiMcpCallContext
{
    private static readonly AsyncLocal<NhAiMcpCallContext?> CurrentContext = new();

    /// <summary>
    /// The calls in progress in this process, for a request of a server that its client handles outside
    /// the asynchronous flow of the call it belongs to.
    /// </summary>
    private static readonly ConcurrentDictionary<NhAiMcpCallContext, byte> InProgress = new();

    private NhAiMcpCallContext(
        string serverId,
        string remoteName,
        NhAiToolDescriptor descriptor,
        NhAiInvocationContext invocation)
    {
        ServerId = serverId;
        RemoteName = remoteName;
        Descriptor = descriptor;
        Invocation = invocation;
    }

    /// <summary>
    /// The call in progress in this asynchronous flow, or <see langword="null"/> outside a governed
    /// call, for example while a client connects or lists tools.
    /// </summary>
    public static NhAiMcpCallContext? Current => CurrentContext.Value;

    /// <summary>
    /// The server id of the import that owns the tool.
    /// </summary>
    public string ServerId { get; }

    /// <summary>
    /// The tool's name on the remote server.
    /// </summary>
    public string RemoteName { get; }

    public NhAiToolDescriptor Descriptor { get; }

    /// <summary>
    /// The context the invocation gate authorized, including its grant source.
    /// </summary>
    public NhAiInvocationContext Invocation { get; }

    /// <summary>
    /// The asynchronous flow of the call, captured when it began, for a request of its server that
    /// the client handles outside that flow.
    /// </summary>
    internal ExecutionContext? Flow { get; private set; }

    /// <summary>
    /// The call's own timeout, which the invoker started; <see langword="null"/> for a call that runs
    /// outside the invoker, such as one a test makes by hand. While the call waits for a person, such as
    /// the person who answers its server's request for input, its clock stands still and the call lets
    /// its concurrency slot go (<see cref="NhAiToolCallTimeout.WaitForPersonAsync"/>).
    /// </summary>
    internal NhAiToolCallTimeout? Timeout { get; private set; }

    /// <summary>
    /// The only governed call of <paramref name="serverId"/> in progress in this process, for a request
    /// of that server that its client handles outside the call's own flow, such as a request for input
    /// of an older protocol revision on the call's stream; <see langword="null"/> when no call or more
    /// than one is in progress, because the request could then belong to someone else's call.
    /// </summary>
    internal static NhAiMcpCallContext? SoleCallOf(string serverId)
    {
        NhAiMcpCallContext? found = null;
        foreach (var call in InProgress.Keys)
        {
            if (!string.Equals(call.ServerId, serverId, StringComparison.Ordinal))
            {
                continue;
            }
            if (found is not null)
            {
                return null;
            }

            found = call;
        }

        return found;
    }

    internal static IDisposable Enter(
        string serverId,
        string remoteName,
        NhAiToolDescriptor descriptor,
        NhAiInvocationContext invocation)
    {
        var previous = CurrentContext.Value;
        var context = new NhAiMcpCallContext(serverId, remoteName, descriptor, invocation);
        CurrentContext.Value = context;
        context.Timeout = NhAiToolCallTimeout.Current;
        context.Flow = ExecutionContext.Capture();
        InProgress.TryAdd(context, 0);
        return new Scope(previous, context);
    }

    private sealed class Scope(NhAiMcpCallContext? previous, NhAiMcpCallContext context) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            InProgress.TryRemove(context, out _);
            CurrentContext.Value = previous;
        }
    }
}
