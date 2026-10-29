using NewHeap.Platform.AI;
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
/// the host without exporting it. SPM-346: it also prepares the milestones server before the host can
/// reach it, with the lookup's reviewed settings, which the first sync keeps. SPM-351: it proposes a new
/// version of the risk review's text to the skills that name an older one, as candidates in shadow.
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
    /// SPM-346: the description the operations code reviewed for the lookup before any sync saw it.
    /// </summary>
    public const string NextMilestoneDescription = "Finds the next milestone of one project.";

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
        var created = await mcpServers.CreateAsync(MilestonesServer(url, apiKey), actor, cancellationToken);
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
    /// SPM-346: prepares the milestones server before this host can reach it, for example while it moves
    /// along with the application. The server is stored with its API key, which nothing returns, the
    /// lookup's reviewed settings (enabled, read-only, the assistant's description) are stored before any
    /// sync has seen the tool, and the server is assigned to the status advisor. Nothing contacts the
    /// server: the lookup stays <c>missing</c>, awaiting its sync, and no turn offers it until the first sync
    /// that sees it.
    /// </summary>
    public async Task<TaskResult<NhAssistantMcpToolDto>> PrepareMilestonesAsync(
        string url,
        string apiKey,
        Guid operatorId,
        CancellationToken cancellationToken)
    {
        var actor = operatorId.ToString("D");
        var created = await mcpServers.CreateAsync(MilestonesServer(url, apiKey), actor, cancellationToken);
        if (!created.Success)
        {
            return TaskResult<NhAssistantMcpToolDto>.Failed(created);
        }

        // A tool that no sync has seen yet keeps these settings until a sync sees it.
        var reviewed = await mcpServers.UpdateToolAsync(
            MilestonesServerId,
            NextMilestoneTool,
            new NhAssistantMcpToolSettingsRequest(true, NhAssistantMcpToolEffects.ReadOnly, NextMilestoneDescription),
            actor,
            cancellationToken);
        if (!reviewed.Success)
        {
            return reviewed;
        }

        var assigned = await mcpServers.AssignAsync(MilestonesServerId, SampleStatusAdvisorComposition.AgentId, actor, cancellationToken);
        return assigned.Success ? reviewed : TaskResult<NhAssistantMcpToolDto>.Failed(assigned);
    }

    /// <summary>
    /// SPM-346: the first sync once the host reaches the milestones server. It takes the lookup's schema
    /// and keeps the settings reviewed before, so the status advisor is offered the lookup at once; a
    /// later change of its schema disables it as for any synced tool.
    /// </summary>
    public async Task<TaskResult<NhAssistantMcpToolDto>> SyncMilestonesAsync(Guid operatorId, CancellationToken cancellationToken)
    {
        var synced = await mcpServers.SyncAsync(MilestonesServerId, operatorId.ToString("D"), cancellationToken);
        if (!synced.Success)
        {
            return TaskResult<NhAssistantMcpToolDto>.Failed(synced);
        }

        return TaskResult<NhAssistantMcpToolDto>.Succeeded(synced.Data!.Single(tool => tool.RemoteName == NextMilestoneTool));
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
    /// SPM-351: once the application publishes <paramref name="version"/> of the risk review's text, the
    /// operations code proposes it to every live data skill that still names an older version. Each skill
    /// gets a candidate in shadow through its own save, with the live version's other fields, which an
    /// administrator compares with the live version and promotes or discards; nothing takes effect by
    /// itself. A skill whose instructions an agent package locks is reported <c>locked</c> and keeps its
    /// version. Agents created in the administration that name an older version are only listed: an
    /// administrator saves each as its next version. A skill keeps its newest candidate: a newer version
    /// replaces the candidate of an older one, and a version that arrives late, for example from a
    /// publication job that ran after the next one, leaves a newer candidate as it is
    /// (<c>newer-proposed</c>), also when both proposals run at once.
    /// </summary>
    public Task<TaskResult<NhAssistantInstructionAssetCandidatesDto>> ProposeRiskReviewVersionAsync(
        NhAiTextAsset version,
        Guid operatorId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(version);
        return assetUsage.ProposeCandidatesAsync(
            SampleInstructionAssetComposition.RiskReviewAsset,
            version.Manifest.Version,
            operatorId.ToString("D"),
            $"Version {version.Manifest.Version} of the risk review",
            cancellationToken);
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

    /// <summary>
    /// The milestones server with its API key, which Data Protection keeps.
    /// </summary>
    private static NhAssistantMcpServerRequest MilestonesServer(string url, string apiKey)
    {
        return new NhAssistantMcpServerRequest(
            MilestonesServerId,
            "Milestones",
            url,
            NhAssistantMcpAuthModes.ApiKeyHeader,
            null,
            apiKey,
            null,
            true);
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
