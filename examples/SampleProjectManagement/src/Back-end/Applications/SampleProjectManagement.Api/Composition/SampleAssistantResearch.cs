using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Chat.AspNet;

namespace SampleProjectManagement.Api.Composition;

/// <summary>
/// Sends the assistant context to one reviewed research MCP tool (SPM-266). The research service
/// keeps its own credentials and scope checks; the context only tells it which agent acts for which
/// person in which conversation, plus the first question and the latest completed messages.
/// </summary>
/// <remarks>
/// Use this when a remote service needs the conversation to answer well and its tool schema should
/// only ask the model for the question. The binding applies only while an administrator connects
/// the server under this id and URL; any other MCP server never receives conversation content.
/// </remarks>
public static class SampleAssistantResearch
{
    public const string ServerId = "research";
    public const string ToolName = "research_ask";
    public const string UrlSetting = "Sample:Assistant:ResearchMcpUrl";

    public static NhAssistantBuilder AddSampleResearchContext(
        this NhAssistantBuilder assistant,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(assistant);
        ArgumentNullException.ThrowIfNull(configuration);
        var url = configuration[UrlSetting];
        if (string.IsNullOrWhiteSpace(url))
        {
            // Without a reviewed research server no MCP call carries the assistant context.
            return assistant;
        }
        return assistant.AddMcpContextBinding(new NhAssistantMcpContextBinding(ServerId, url, ToolName)
        {
            Snapshot = new NhAssistantConversationSnapshotRequest
            {
                MaxRecentMessages = 4,
                MaxBytes = 4_096,
                MaxMessageBytes = 1_024
            }
        });
    }
}
