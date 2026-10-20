using System.Net;
using Microsoft.Extensions.AI;

namespace NewHeap.Platform.AI.Chat.Runtime;

/// <summary>
/// Governed functions imported from the MCP servers assigned to an agent for one turn.
/// Disposing releases connections that belong to the turn only.
/// </summary>
internal sealed class NhAssistantMcpToolSet(
    IReadOnlyList<AIFunction> functions,
    Func<ValueTask>? dispose = null) : IAsyncDisposable
{
    public static NhAssistantMcpToolSet Empty { get; } = new([]);

    public IReadOnlyList<AIFunction> Functions { get; } = functions;

    public IReadOnlyList<NhAiToolDescriptor> Descriptors { get; } = functions
        .OfType<INhAiGovernedAIFunction>()
        .Select(function => function.Descriptor)
        .ToArray();

    /// <summary>
    /// The assigned servers whose tools the turn does not offer, with the stable code why.
    /// </summary>
    public IReadOnlyDictionary<string, string> SkippedServers { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// The servers the person of a request turn has to connect, or connect again, before the agent can
    /// use them fully.
    /// </summary>
    public IReadOnlyList<NhAssistantMcpConnectionNeeded> ConnectionsNeeded { get; init; } = [];

    /// <summary>
    /// Whose credential the tools of each loaded server carry: <c>organization</c>, a person's actor
    /// id or <c>none</c> (<see cref="NhAssistantAuditEvent.McpConnectionOwner"/>).
    /// </summary>
    public IReadOnlyDictionary<string, string> ConnectionOwners { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// The code why the server of an <c>mcp.&lt;server&gt;.&lt;tool&gt;</c> tool id was skipped, or
    /// <see langword="null"/>.
    /// </summary>
    public string? SkippedCodeOf(string toolId)
    {
        var parts = toolId.Split('.', 3);
        if (parts.Length < 3 || parts[0] != "mcp")
        {
            return null;
        }
        return SkippedServers.TryGetValue(parts[1], out var code) ? code : null;
    }

    public ValueTask DisposeAsync()
    {
        return dispose?.Invoke() ?? ValueTask.CompletedTask;
    }
}

/// <summary>
/// A server the person of a request turn has to connect: its id, display name and the reason
/// (<see cref="NhAssistantMcpConnectionReasons"/>).
/// </summary>
internal sealed record NhAssistantMcpConnectionNeeded(string ServerId, string DisplayName, string Reason);

/// <summary>
/// Who a turn's MCP tools are loaded for.
/// </summary>
/// <param name="Agent">The agent whose assigned servers are loaded.</param>
internal sealed record NhAssistantMcpToolRequest(NhAssistantAgent Agent)
{
    /// <summary>
    /// The turn runs without a person's request: a headless agent run or the execution of its
    /// deferred proposal. Only servers that allow headless use are loaded.
    /// </summary>
    public bool IsHeadless { get; init; }

    /// <summary>
    /// The person the turn's personal credentials belong to. In a request turn the person who sent
    /// the message. In a headless turn the person whose rights the turn uses: the initiator when the
    /// agent delegates from the initiator, otherwise the account it answers to when it acts with that
    /// account's rights; <see langword="null"/> when the agent acts for itself.
    /// </summary>
    public string? PersonActorId { get; init; }

    /// <summary>
    /// Which rights of that person the invocation gate uses: <see cref="NhAiGrantSource.Delegated"/>
    /// for the initiator, <see cref="NhAiGrantSource.Accountable"/> for the accountable account.
    /// </summary>
    public NhAiGrantSource PersonGrantSource { get; init; }

    /// <summary>
    /// The subject of a headless turn, for building the person's principal.
    /// </summary>
    public NhAiSubjectInfo? Subject { get; init; }
}

/// <summary>
/// Supplies the enabled tools of the MCP servers assigned to an agent. An unreachable server is
/// skipped so the turn continues with the remaining tools.
/// </summary>
internal interface INhAssistantMcpToolSource
{
    Task<NhAssistantMcpToolSet> GetToolsAsync(
        NhAssistantMcpToolRequest request,
        CancellationToken cancellationToken);
}

internal sealed class NhAssistantNoMcpToolSource : INhAssistantMcpToolSource
{
    public Task<NhAssistantMcpToolSet> GetToolsAsync(NhAssistantMcpToolRequest request, CancellationToken cancellationToken)
    {
        return Task.FromResult(NhAssistantMcpToolSet.Empty);
    }
}

/// <summary>
/// A failure of an MCP connection or request with a stable code. Its message never contains
/// remote text, credentials or addresses.
/// </summary>
internal class NhAssistantMcpException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Maps an MCP connection or call failure to a stable code without exposing its message.
/// </summary>
internal static class NhAssistantMcpFailures
{
    public static string Classify(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is NhAssistantMcpException failure)
            {
                return failure.Code;
            }
            if (current is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden })
            {
                return NhAssistantAdminErrorCodes.McpUnauthorized;
            }
        }
        return NhAssistantAdminErrorCodes.McpUnreachable;
    }

    /// <summary>
    /// Whether a failure with this code stopped the request before it left the host, so the remote
    /// tool certainly did not run.
    /// </summary>
    public static bool StoppedBeforeSending(string code)
    {
        return code is NhAssistantAdminErrorCodes.McpHostBlocked or NhAssistantAdminErrorCodes.McpCredentialMismatch;
    }
}
