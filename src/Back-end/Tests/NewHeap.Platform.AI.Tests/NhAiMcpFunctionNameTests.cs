using NewHeap.Platform.AI.Mcp;
using Xunit;

namespace NewHeap.Platform.AI.Tests;

/// <summary>
/// The function name of an imported MCP tool, <c>mcp_&lt;namespace&gt;_&lt;tool id&gt;</c>, is injective:
/// the namespace keeps its dashes and the tool id's dashes become underscores, so no two tools of
/// different namespaces or ids share a name, while a namespace without dashes keeps the names it had.
/// </summary>
public sealed class NhAiMcpFunctionNameTests
{
    [Theory]
    [InlineData("planning", "lookup-order", "mcp_planning_lookup_order")]
    [InlineData("acme", "billing-refund", "mcp_acme_billing_refund")]
    [InlineData("acme-billing", "refund", "mcp_acme-billing_refund")]
    [InlineData("team-calendar", "next-milestone", "mcp_team-calendar_next_milestone")]
    public void A_name_keeps_the_namespace_and_joins_the_tool_id_with_underscores(string toolNamespace, string toolId, string name)
    {
        Assert.Equal(name, NhAiGovernedMcpClientFunction.FunctionNameOf(toolNamespace, toolId));
    }

    [Fact]
    public void No_two_tools_share_a_name()
    {
        string[] segments = ["a", "b", "a-b", "b-a", "a-b-c", "c", "ab"];
        var pairs = segments.SelectMany(toolNamespace => segments.Select(toolId => (toolNamespace, toolId))).ToArray();

        var names = pairs.Select(pair => NhAiGovernedMcpClientFunction.FunctionNameOf(pair.toolNamespace, pair.toolId)).ToArray();

        Assert.Equal(pairs.Length, names.Distinct(StringComparer.Ordinal).Count());
        Assert.All(names, name => Assert.Matches("^[a-zA-Z0-9_-]+$", name));
    }
}
