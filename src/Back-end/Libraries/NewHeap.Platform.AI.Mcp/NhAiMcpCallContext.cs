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

    internal static IDisposable Enter(
        string serverId,
        string remoteName,
        NhAiToolDescriptor descriptor,
        NhAiInvocationContext invocation)
    {
        var previous = CurrentContext.Value;
        CurrentContext.Value = new NhAiMcpCallContext(serverId, remoteName, descriptor, invocation);
        return new Scope(previous);
    }

    private sealed class Scope(NhAiMcpCallContext? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            CurrentContext.Value = previous;
        }
    }
}
