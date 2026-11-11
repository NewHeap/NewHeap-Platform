using NewHeap.Platform.AI;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Chat.AspNet;

namespace SampleProjectManagement.Api.Composition;

/// <summary>
/// Offers the reviewed research tool to the API's own MCP clients, such as a person's AI app, next to
/// the API's own tools (SPM-399). Without <see cref="EnabledSetting"/> no imported tool reaches an MCP
/// client.
/// </summary>
/// <remarks>
/// Use this when people should call a coupled application's read tools from their own AI app without a
/// turn of the assistant in between. NewHeap only exports read-only tools of servers with the
/// organization's credential, imports them for the caller as in that person's own turn, and checks the
/// server's required policy and the host's discovery policy per caller; the selector only names which
/// reviewed tools the export offers and under which name.
/// </remarks>
public static class SampleImportedMcpToolExport
{
    public const string EnabledSetting = "Sample:Assistant:ExportResearchToolsToMcp";

    public static NhAssistantBuilder AddSampleImportedMcpToolExport(
        this NhAssistantBuilder assistant,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(assistant);
        ArgumentNullException.ThrowIfNull(configuration);
        if (!configuration.GetValue<bool>(EnabledSetting)
            || string.IsNullOrWhiteSpace(configuration[SampleAssistantResearch.UrlSetting]))
        {
            return assistant;
        }

        return assistant.ExportImportedMcpTools<SampleResearchToolExportSelector>();
    }
}

/// <summary>
/// Exports the tools of the research server that the composition reviewed, under its reviewed id and URL
/// only, with their remote names. A copy of the same service under another id stays with the assistant.
/// </summary>
public sealed class SampleResearchToolExportSelector(IConfiguration configuration) : INhAssistantImportedMcpToolExportSelector
{
    public ValueTask<NhAssistantImportedMcpToolExport?> SelectAsync(
        NhAssistantExportableMcpTool tool,
        NhAiInvocationContext caller,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var reviewedUrl = configuration[SampleAssistantResearch.UrlSetting];
        var export = tool.ServerId == SampleAssistantResearch.ServerId
                     && string.Equals(tool.ServerUrl, reviewedUrl, StringComparison.OrdinalIgnoreCase)
            ? new NhAssistantImportedMcpToolExport(tool.RemoteName)
            : null;
        return ValueTask.FromResult(export);
    }
}
