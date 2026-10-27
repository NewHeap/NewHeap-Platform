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
    /// The enabled tools of loaded servers that the turn does not offer, by tool id
    /// (<c>mcp.&lt;server&gt;.&lt;tool&gt;</c>), with the stable code why, such as
    /// <see cref="NhAssistantAdminErrorCodes.McpToolNameConflict"/>.
    /// </summary>
    public IReadOnlyDictionary<string, string> SkippedTools { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

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
    /// The personal servers a headless turn left out because a person's consent to background use or
    /// connection is missing, or because the turn acts for someone other than the person who asked.
    /// </summary>
    public IReadOnlyList<NhAssistantMcpMissingConnection> MissingConnections { get; init; } = [];

    /// <summary>
    /// The code why the tool of an <c>mcp.&lt;server&gt;.&lt;tool&gt;</c> tool id, or its server, was
    /// skipped, or <see langword="null"/>.
    /// </summary>
    public string? SkippedCodeOf(string toolId)
    {
        if (SkippedTools.TryGetValue(toolId, out var toolCode))
        {
            return toolCode;
        }

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
/// A personal server a headless turn left out, with the skip code
/// (<see cref="NewHeap.Platform.AI.Chat.Runs.NhAssistantRunMcpSkips.IsPersonal"/>) and the person whose consent or connection
/// would let it serve; <see langword="null"/> when none would, because the turn acts for someone other
/// than the person who asked.
/// </summary>
internal sealed record NhAssistantMcpMissingConnection(string ServerId, string DisplayName, string Code, string? PersonActorId);

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

    /// <summary>
    /// Why no personal credential may serve this headless turn, or <see langword="null"/> when
    /// <see cref="PersonActorId"/>'s may (with that person's consent). A turn a person asked for (a
    /// run started by hand, an agent request, or a message typed while the run worked) uses only that
    /// person's own connection: when it acts for someone else, servers with personal background use
    /// are left out with <see cref="NhAssistantAdminErrorCodes.McpInitiatorOnly"/>.
    /// </summary>
    public string? PersonalCredentialRefusal { get; init; }
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
internal class NhAssistantMcpException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;

    /// <summary>
    /// Whether the remote tool certainly did not run: the request never left the host (a blocked
    /// target, a call the credential may not carry, a credential that could not be obtained), or the
    /// server refused the request's credential before it handled it and no renewed credential could
    /// repeat it. An approved proposal whose execution failed so ends <c>execution-failed</c>, not
    /// <c>execution-unknown</c>.
    /// </summary>
    public bool ToolNotRun { get; init; }
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

    /// <summary>
    /// Whether the failure of an MCP call shows that the remote tool certainly did not run: an
    /// <see cref="NhAssistantMcpException"/> marked <see cref="NhAssistantMcpException.ToolNotRun"/> or
    /// with a code that stops a request before it is sent, or a request the server refused with
    /// <c>401</c> or <c>403</c>. Every other failure may have run the tool.
    /// </summary>
    public static bool ToolNotRun(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is NhAssistantMcpException failure)
            {
                return failure.ToolNotRun || StoppedBeforeSending(failure.Code);
            }
            if (current is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden })
            {
                return true;
            }
        }
        return false;
    }
}
