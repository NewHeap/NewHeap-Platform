using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.AspNet.Mcp;
using NewHeap.Platform.Common.Models;
using SampleProjectManagement.Api.Composition;

namespace SampleProjectManagement.Api.Services;

/// <summary>
/// SPM-331: the sample's operations code changes the assistant through the platform's services, never
/// through the administration's endpoints or its tables, so the checks, the protected secrets and the
/// audit of the administration apply. It connects the milestones MCP server to the status advisor,
/// finds which skills and agents use the risk review's text, and compares a shipped agent package with
/// the host without exporting it.
/// </summary>
public sealed class SampleAssistantOperationsService(
    INhAssistantMcpServers mcpServers,
    INhAssistantInstructionAssetUsage assetUsage,
    INhAssistantAgentPackages packages)
{
    /// <summary>
    /// The MCP server that knows the milestones of projects.
    /// </summary>
    public const string MilestonesServerId = "milestones";

    /// <summary>
    /// Its read-only tool, which the status advisor may call.
    /// </summary>
    public const string NextMilestoneTool = "next_milestone";

    /// <summary>
    /// Connects the milestones server with its API key, which Data Protection keeps and nothing
    /// returns, syncs its tools, enables the milestone lookup as read-only and assigns the server to the
    /// status advisor. The assignment leaves the advisor's definition and version as they are; every
    /// step is audited as the administration audits it.
    /// </summary>
    public async Task<TaskResult<NhAssistantMcpServerDto>> ConnectMilestonesAsync(
        string url,
        string apiKey,
        Guid operatorId,
        CancellationToken cancellationToken)
    {
        var actor = operatorId.ToString("D");
        var created = await mcpServers.CreateAsync(
            new NhAssistantMcpServerRequest(
                MilestonesServerId,
                "Milestones",
                url,
                NhAssistantMcpAuthModes.ApiKeyHeader,
                null,
                apiKey,
                null,
                true),
            actor,
            cancellationToken);
        if (!created.Success)
        {
            return created;
        }

        // Synced tools start disabled as mutations; the lookup only reads.
        var synced = await mcpServers.SyncAsync(MilestonesServerId, actor, cancellationToken);
        if (!synced.Success)
        {
            return TaskResult<NhAssistantMcpServerDto>.Failed(synced);
        }

        var enabled = await mcpServers.UpdateToolAsync(
            MilestonesServerId,
            NextMilestoneTool,
            new NhAssistantMcpToolSettingsRequest(true, NhAssistantMcpToolEffects.ReadOnly),
            actor,
            cancellationToken);
        if (!enabled.Success)
        {
            return TaskResult<NhAssistantMcpServerDto>.Failed(enabled);
        }

        return await mcpServers.AssignAsync(MilestonesServerId, SampleStatusAdvisorComposition.AgentId, actor, cancellationToken);
    }

    /// <summary>
    /// Takes the milestones server from the status advisor and deletes it with its tools; a server that
    /// is already gone counts as disconnected.
    /// </summary>
    public async Task<TaskResult> DisconnectMilestonesAsync(Guid operatorId, CancellationToken cancellationToken)
    {
        var actor = operatorId.ToString("D");
        var unassigned = await mcpServers.UnassignAsync(MilestonesServerId, SampleStatusAdvisorComposition.AgentId, actor, cancellationToken);
        if (IsServerMissing(unassigned))
        {
            return TaskResult.Succeeded();
        }
        if (!unassigned.Success)
        {
            return unassigned;
        }

        return await mcpServers.DeleteAsync(MilestonesServerId, actor, cancellationToken);
    }

    /// <summary>
    /// The data skills and agents that use the risk review's text (SPM-320), with the version of the text
    /// each one names. A new version of the text changes none of them: an administrator saves it as their
    /// next version.
    /// </summary>
    public Task<TaskResult<NhAssistantInstructionAssetUsageDto>> ListRiskReviewUsageAsync(CancellationToken cancellationToken)
    {
        return assetUsage.ListAsync(SampleInstructionAssetComposition.RiskReviewAsset, cancellationToken);
    }

    /// <summary>
    /// Which parts of the shipped package's agents the host has otherwise, by comparing the package's
    /// hashes with those of the host's agents as an export would build them, and the version of the
    /// package the host installed. Nothing is exported, so nothing is audited; the package must carry
    /// its hashes, as every export writes them.
    /// </summary>
    public async Task<TaskResult<SamplePackageComparison>> CompareWithHostAsync(
        NhAssistantAgentPackage shipped,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(shipped);
        var installed = await packages.GetPackageAsync(shipped.Package.Id, cancellationToken);
        var read = await packages.ReadAsync(shipped.Agents.Select(agent => agent.Id).ToArray(), cancellationToken);
        if (!read.Success)
        {
            return TaskResult<SamplePackageComparison>.Failed(read);
        }

        var different = new List<string>();
        foreach (var agent in shipped.Agents)
        {
            var host = read.Data!.Single(item => item.Id == agent.Id).Hashes ?? new Dictionary<string, string>();
            var package = agent.Hashes ?? new Dictionary<string, string>();
            foreach (var part in host.Keys.Union(package.Keys).Order(StringComparer.Ordinal))
            {
                if (!string.Equals(host.GetValueOrDefault(part), package.GetValueOrDefault(part), StringComparison.Ordinal))
                {
                    different.Add(agent.Id + "/" + part);
                }
            }
        }

        return TaskResult<SamplePackageComparison>.Succeeded(new SamplePackageComparison(
            shipped.Package.Id,
            installed?.Version,
            shipped.Package.Version,
            different));
    }

    private static bool IsServerMissing(TaskResult result)
    {
        return result.GetResultItems().Any(item => item.Name == NhAssistantAdminErrorCodes.McpServerNotFound);
    }
}

/// <summary>
/// SPM-331: a shipped agent package compared with the host.
/// </summary>
/// <param name="PackageId">The package.</param>
/// <param name="InstalledVersion">The version the host installed; <see langword="null"/> when it never
/// imported the package.</param>
/// <param name="ShippedVersion">The version of the shipped package.</param>
/// <param name="DifferentParts">The parts the host has otherwise, as <c>agent-id/part</c>, such as
/// <c>sample-risk-desk/agent</c>.</param>
public sealed record SamplePackageComparison(
    string PackageId,
    int? InstalledVersion,
    int ShippedVersion,
    IReadOnlyList<string> DifferentParts);
