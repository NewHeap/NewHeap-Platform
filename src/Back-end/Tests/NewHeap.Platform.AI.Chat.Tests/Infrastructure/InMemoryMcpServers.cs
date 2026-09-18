using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO.Pipelines;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NewHeap.Platform.AI.Chat.AspNet.Mcp;

namespace NewHeap.Platform.AI.Chat.Tests.Infrastructure;

/// <summary>
/// Official MCP SDK servers connected in memory. The assistant's connection planning (SSRF checks
/// and authentication headers) still runs; only the transport is replaced.
/// </summary>
internal sealed class InMemoryMcpServers : INhAssistantMcpClientFactory
{
    private readonly ConcurrentDictionary<string, Func<IReadOnlyList<McpServerTool>>> _servers = new(StringComparer.Ordinal);

    public ConcurrentQueue<NhAssistantMcpConnectionPlan> Connections { get; } = new();

    public ConcurrentQueue<string> Calls { get; } = new();

    public void Register(string url, Func<IReadOnlyList<McpServerTool>> tools)
    {
        _servers[url] = tools;
    }

    public async Task<McpClient> ConnectAsync(NhAssistantMcpConnectionPlan plan, CancellationToken cancellationToken)
    {
        Connections.Enqueue(plan);
        if (!_servers.TryGetValue(plan.Endpoint.ToString(), out var tools))
        {
            throw new HttpRequestException("No server.");
        }
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var server = McpServer.Create(
            new StreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream()),
            new McpServerOptions { ScopeRequests = false, ToolCollection = [.. tools()] });
        _ = server.RunAsync(CancellationToken.None);
        return await McpClient.CreateAsync(
            new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream()),
            cancellationToken: cancellationToken);
    }

    public IReadOnlyList<McpServerTool> PlanningTools(bool withPriority = false)
    {
        var lookup = McpServerTool.Create(
            ([Description("Order number")] string order) =>
            {
                Calls.Enqueue("lookup-order");
                return $"Order {order} is planned for Monday.";
            },
            new McpServerToolCreateOptions { Name = "Lookup_Order", Description = "Look up the planning of an order.", ReadOnly = true });
        var reschedule = withPriority
            ? McpServerTool.Create(
                (string order, string day, int priority) =>
                {
                    Calls.Enqueue("reschedule");
                    return $"Order {order} moved to {day} with priority {priority}.";
                },
                new McpServerToolCreateOptions { Name = "reschedule", Description = "Move an order to another day." })
            : McpServerTool.Create(
                (string order, string day) =>
                {
                    Calls.Enqueue("reschedule");
                    return $"Order {order} moved to {day}.";
                },
                new McpServerToolCreateOptions { Name = "reschedule", Description = "Move an order to another day." });
        return [lookup, reschedule];
    }
}
