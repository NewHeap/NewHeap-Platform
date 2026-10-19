using System.Diagnostics.Metrics;

namespace NewHeap.Platform.AI.Chat.AspNet.Mcp;

/// <summary>
/// MCP counters on the <c>NewHeap.Platform.AI</c> meter, tagged with an outcome code only: never a
/// server address, a person, a scope or a token.
/// </summary>
internal static class NhAssistantMcpMetrics
{
    /// <summary>The outcome of a request or authorization that succeeded.</summary>
    public const string Ok = "ok";

    private static readonly Meter Meter = new("NewHeap.Platform.AI");

    private static readonly Counter<long> Requests = Meter.CreateCounter<long>(
        "nh.ai.mcp.requests",
        description: "HTTP requests of the assistant to MCP servers, by outcome.");

    private static readonly Counter<long> TokenRequests = Meter.CreateCounter<long>(
        "nh.ai.mcp.token_requests",
        description: "Token requests of the assistant to OAuth authorization servers, by grant and outcome.");

    private static readonly Counter<long> Authorizations = Meter.CreateCounter<long>(
        "nh.ai.mcp.authorizations",
        description: "OAuth authorizations of MCP connections, by outcome.");

    /// <summary><c>ok</c>, <c>renewed</c>, <c>http-&lt;status&gt;</c> or a failure code.</summary>
    public static void Request(string outcome)
    {
        Requests.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
    }

    public static void TokenRequest(string grantType, string outcome)
    {
        TokenRequests.Add(
            1,
            new KeyValuePair<string, object?>("grant", grantType),
            new KeyValuePair<string, object?>("outcome", outcome));
    }

    /// <summary><c>completed</c> or the failure code of the callback or the completion.</summary>
    public static void Authorization(string outcome)
    {
        Authorizations.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
    }
}
