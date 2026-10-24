using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// Agent packages (S7-2): agents with their configuration as data, exported as one versioned package,
/// previewed and imported on another host through the same validators as saving, with parts the
/// package locks against local changes until an administrator unlinks them. The endpoints under
/// <c>admin/agent-packages</c> and <c>admin/agents/{id}/package/unlink</c> use it, behind the
/// administration policy; applications call it as well, for example to ship packages with a deploy.
/// </summary>
public interface INhAssistantAgentPackages
{
    /// <summary>
    /// Exports exactly <paramref name="agentIds"/> (1 to 20) as one package (PK13, PK18): agents created
    /// in the administration whole, agents in code as an overlay of their data; never history,
    /// candidates, runs, run state, shadow overrides, MCP servers, the technical account or secrets.
    /// The options set the package id, version and locks; exporting linked agents keeps their package
    /// id. References to agents outside the selection are returned as warnings. An unknown agent fails
    /// with <c>assistant-agent-not-found</c>, invalid options with field errors. Audited as
    /// <c>AdminAgentPackageExported</c>.
    /// </summary>
    Task<TaskResult<NhAssistantAgentPackageExportDto>> ExportAsync(
        IReadOnlyList<string> agentIds,
        NhAssistantAgentPackageExportOptions options,
        string actorId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// What <see cref="ImportAsync"/> would do, per agent and part (PK6): <c>created</c>,
    /// <c>changed</c>, <c>unchanged</c>, <c>kept-local</c>, <c>left-behind</c> or <c>refused</c> with
    /// field errors, after one check of the whole package against the host as it would be after the
    /// import (PK17). Changes nothing. A package this host does not read fails with
    /// <c>assistant-package-format-unsupported</c>, one that is too large with
    /// <c>assistant-package-too-large</c>.
    /// </summary>
    Task<TaskResult<NhAssistantAgentPackagePreviewDto>> PreviewImportAsync(
        NhAssistantAgentPackage package,
        NhAssistantAgentPackageImportOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Imports the package in one database transaction with one change of the run configuration and one
    /// reload (PK6): every agent and part of it, or nothing. Fails as <see cref="PreviewImportAsync"/>
    /// and with <c>assistant-package-refused</c> and the field error <c>parts: invalid</c> when a part is
    /// refused; the preview of the package says why per part. Every part is audited as its save, then
    /// the package as <c>AdminAgentPackageImported</c>.
    /// </summary>
    Task<TaskResult<NhAssistantAgentPackageImportDto>> ImportAsync(
        NhAssistantAgentPackage package,
        NhAssistantAgentPackageImportOptions options,
        string actorId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Unlinks one part of an agent (<paramref name="part"/> a key of
    /// <see cref="NhAssistantPackagePartKinds"/>), or the whole agent (<see langword="null"/>), from its
    /// package (PK10): its locks no longer hold and the next upgrade keeps it as <c>kept-local</c>
    /// unless the import relinks it. Fails with <c>assistant-agent-not-found</c>,
    /// <c>assistant-package-not-linked</c> for an agent or part without a link and a field error
    /// <c>part: invalid</c> for an unknown part key. Audited as <c>AdminAgentPackageUnlinked</c>.
    /// </summary>
    Task<TaskResult<NhAssistantAgentPackageUnlinkDto>> UnlinkAsync(
        string agentId,
        string? part,
        string actorId,
        string? reason = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The package the agent belongs to with its linked parts; <see langword="null"/> for an agent
    /// that belongs to none.
    /// </summary>
    Task<NhAssistantAgentPackageLinkDto?> GetLinkAsync(string agentId, CancellationToken cancellationToken = default);
}
