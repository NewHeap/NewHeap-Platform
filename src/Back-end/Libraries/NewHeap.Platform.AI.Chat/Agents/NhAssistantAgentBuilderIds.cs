namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// The names of the platform's agent builder (RB-7 to RB-9): the assistant agent in the administration
/// that helps an administrator build a whole agent, which <c>AddAgentBuilder</c> of
/// <c>NewHeap.Platform.AI.Chat.AspNet</c> switches on. Its id, its tools and its policy are reserved: no
/// agent created in the administration takes the id, no other agent is offered its tools, whatever its
/// tool selectors say, and its definition never changes in the administration.
/// </summary>
public static class NhAssistantAgentBuilderIds
{
    /// <summary>
    /// The id of the agent builder.
    /// </summary>
    public const string AgentId = "nh-agent-builder";

    /// <summary>
    /// The prefix of the ids of the agent builder's tools, such as
    /// <c>assistant.agent-builder.save-rule-script</c>.
    /// </summary>
    public const string ToolIdPrefix = "assistant.agent-builder.";

    /// <summary>
    /// The authorization policy the agent builder and its tools require: the assistant's access policy
    /// and its administration policy together, as the administration's endpoints require them.
    /// </summary>
    public const string Policy = "nh-assistant-agent-builder";

    /// <summary>
    /// Whether <paramref name="agentId"/> is the agent builder.
    /// </summary>
    public static bool IsAgentBuilder(string? agentId)
    {
        return string.Equals(agentId, AgentId, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether <paramref name="toolId"/> is one of the agent builder's tools.
    /// </summary>
    public static bool IsAgentBuilderTool(string? toolId)
    {
        return toolId is not null && toolId.StartsWith(ToolIdPrefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether a turn of <paramref name="agentId"/> may be offered <paramref name="toolId"/>: every tool but
    /// the agent builder's, which only the agent builder is offered.
    /// </summary>
    internal static bool MayOffer(string agentId, string toolId)
    {
        return !IsAgentBuilderTool(toolId) || IsAgentBuilder(agentId);
    }
}
