using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Chat.AspNet;

namespace SampleProjectManagement.Api.Composition;

/// <summary>
/// The links between applications that the API reviewed and stores (SPM-266, SPM-366, FB59): partner
/// applications whose consultation chains it continues, and research tools of administrator-connected MCP
/// servers that receive the assistant context. They count next to the code registrations of
/// <see cref="SampleConsultationChain"/> and <see cref="SampleAssistantResearch"/>, so a partner that is
/// linked or revoked while the API runs counts within <c>NewHeap:AI:Assistant:Mcp:LinkCacheDuration</c>
/// (30 seconds by default), without a restart.
/// </summary>
/// <remarks>
/// <para>
/// The sample keeps its reviewed links under <see cref="Section"/>, which an operator changes while the API
/// runs (<c>appsettings.json</c> reloads on change). An application with a review screen keeps them in a
/// table of its own and lists them in the same way; who may create a link is the application's decision,
/// behind its own permission. A revoked link stays in the list with <c>Revoked</c> set, so its review
/// history is kept.
/// </para>
/// <para>
/// NewHeap checks every entry as it checks the same registration in code and leaves out one it would
/// refuse, with a warning; a link never grants more than a code registration could. A stored binding
/// applies only to a tool of an administrator-connected server under the binding's server id and URL, and
/// a stored partner is believed only for MCP calls whose token carries its issuer.
/// </para>
/// </remarks>
public static class SampleMcpLinks
{
    /// <summary>The configuration section of the reviewed links.</summary>
    public const string Section = "Sample:Assistant:McpLinks";

    /// <summary>
    /// The partner applications whose chains the API continues: <c>ApplicationId</c>, <c>Issuer</c> and,
    /// for an issuer that serves several applications, <c>ClientClaimType</c> and <c>ClientId</c>.
    /// </summary>
    public const string ChainCallersKey = "ChainCallers";

    /// <summary>
    /// The research tools that receive the assistant context: <c>ServerId</c>, <c>ServerUrl</c> and
    /// <c>RemoteName</c>, as an administrator connects the server.
    /// </summary>
    public const string ContextBindingsKey = "ContextBindings";

    /// <summary>Set to <c>true</c> on a link that was revoked.</summary>
    public const string RevokedKey = "Revoked";

    public static NhAssistantBuilder AddSampleMcpLinks(this NhAssistantBuilder assistant)
    {
        ArgumentNullException.ThrowIfNull(assistant);
        return assistant.UseMcpLinks<SampleConfiguredMcpLinks>();
    }
}

/// <summary>
/// Lists the API's reviewed links from <see cref="SampleMcpLinks.Section"/>, read on every call, so the
/// assistant's own cache decides when a change counts. It answers with the API's links only, never with
/// anything of the request.
/// </summary>
public sealed class SampleConfiguredMcpLinks(IConfiguration configuration) : INhAssistantMcpLinks
{
    public ValueTask<NhAssistantMcpLinkList> ListAsync(CancellationToken cancellationToken)
    {
        var section = configuration.GetSection(SampleMcpLinks.Section);
        var callers = Active(section.GetSection(SampleMcpLinks.ChainCallersKey))
            .Select(link => new NhAssistantMcpChainCaller(link["ApplicationId"] ?? string.Empty, link["Issuer"] ?? string.Empty)
            {
                ClientClaimType = link["ClientClaimType"],
                ClientId = link["ClientId"]
            })
            .ToArray();
        // A stored research binding sends what the code binding of SampleAssistantResearch sends.
        var bindings = Active(section.GetSection(SampleMcpLinks.ContextBindingsKey))
            .Select(link => new NhAssistantMcpContextBinding(
                link["ServerId"] ?? string.Empty,
                link["ServerUrl"] ?? string.Empty,
                link["RemoteName"] ?? string.Empty)
            {
                Snapshot = new NhAssistantConversationSnapshotRequest
                {
                    MaxRecentMessages = 4,
                    MaxBytes = 4_096,
                    MaxMessageBytes = 1_024
                },
                IncludeChain = SampleConsultationChain.IsNamed(configuration)
            })
            .ToArray();
        return ValueTask.FromResult(new NhAssistantMcpLinkList(callers, bindings));
    }

    private static IEnumerable<IConfigurationSection> Active(IConfigurationSection links)
    {
        return links.GetChildren()
            .Where(link => !bool.TryParse(link[SampleMcpLinks.RevokedKey], out var revoked) || !revoked);
    }
}
