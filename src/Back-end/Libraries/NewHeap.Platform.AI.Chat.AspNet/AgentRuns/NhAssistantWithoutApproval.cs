using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// The administrator's key of the one exception to "an agent's effect waits for a person" (FB82): lets
/// one agent run one tool without approval, once the tool allows it in code
/// (<see cref="NhAiToolAttribute.AllowWithoutApproval"/>). A switch goes from off to shadow, where every
/// call still becomes a proposal and the platform records that it would have run without approval, and
/// only from shadow to live, where the agent's own work runs the tool without a proposal and every call
/// is audited as run without approval with the switch's version. Every change is the next version of the
/// switch, stored with who made it, and audited as
/// <see cref="NhAssistantAuditEventKind.AdminAgentToolWithoutApprovalChanged"/>; switching off, or back to
/// shadow, applies to the next call on every host. A switch applies to the tool contract it was made for:
/// a tool whose contract changed reads as off until it is switched again, first in shadow.
/// <c>GET admin/agents/{id}/without-approval</c>, <c>PUT admin/agents/{id}/tools/{toolId}/without-approval</c>
/// and <c>GET .../without-approval/versions</c> use it behind the administration policy.
/// </summary>
public interface INhAssistantWithoutApprovalSwitches
{
    /// <summary>
    /// The agent's tools that allow running without approval, with their switches; <see langword="null"/>
    /// when the agent does not exist.
    /// </summary>
    Task<NhAssistantWithoutApprovalListDto?> GetAsync(string agentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every version of the agent's switch for the tool, newest first; empty when nobody switched it.
    /// </summary>
    Task<IReadOnlyList<NhAssistantWithoutApprovalVersionDto>> GetVersionsAsync(
        string agentId,
        string toolId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Switches the tool for the agent to <see cref="NhAssistantWithoutApprovalRequest.Mode"/> when
    /// <see cref="NhAssistantWithoutApprovalRequest.ExpectedVersion"/> is the current version (0 or
    /// <see langword="null"/> before the first switch). Fails with <c>assistant-agent-not-found</c>,
    /// <c>assistant-without-approval-not-allowed</c> (the tool does not allow it in code, is not among the
    /// agent's tools, or the agent does no work of its own), <c>assistant-without-approval-shadow-first</c>
    /// (live from anything but shadow), <c>assistant-version-conflict</c>, or field errors for
    /// <c>mode</c> and <c>reason</c>. The same mode again changes nothing.
    /// </summary>
    /// <param name="actorId">Who switches: a person's actor id or the application's own actor, at most
    /// 256 characters.</param>
    Task<TaskResult<NhAssistantWithoutApprovalSwitchDto>> SetAsync(
        string agentId,
        string toolId,
        NhAssistantWithoutApprovalRequest request,
        string actorId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A change of the switch that lets an agent run a tool without approval.
/// </summary>
/// <param name="Mode"><c>off</c>, <c>shadow</c> or <c>live</c> (<see cref="NhAssistantWithoutApprovalModes"/>).</param>
/// <param name="ExpectedVersion">The switch's current version, 0 or <see langword="null"/> before the first switch.</param>
/// <param name="Reason">Why, at most 500 characters; kept with the version and left out of the business audit.</param>
public sealed record NhAssistantWithoutApprovalRequest(
    string? Mode,
    int? ExpectedVersion,
    string? Reason = null);

/// <summary>
/// One tool of an agent that allows running without approval, with the switch in effect.
/// </summary>
/// <param name="ToolId">The tool.</param>
/// <param name="Description">The tool's description in code.</param>
/// <param name="Effect">The tool's effect: <c>idempotent-mutation</c> or <c>mutation</c>.</param>
/// <param name="Mode">The mode in effect: <c>off</c>, <c>shadow</c> or <c>live</c>; <c>off</c> while the
/// tool's contract differs from the one the switch was made for.</param>
/// <param name="Version">The switch's version, 0 when nobody switched it; the next change expects it.</param>
/// <param name="ContractChanged">Whether the tool's contract changed since the switch was made, which
/// makes it read as off until it is switched again, first in shadow.</param>
/// <param name="ChangedBy">Who switched it last.</param>
/// <param name="ChangedAt">When it was switched last.</param>
/// <param name="Reason">The reason given with the last switch.</param>
public sealed record NhAssistantWithoutApprovalSwitchDto(
    string ToolId,
    string Description,
    string Effect,
    string Mode,
    int Version,
    bool ContractChanged,
    string? ChangedBy,
    DateTimeOffset? ChangedAt,
    string? Reason);

/// <summary>
/// The agent's tools that allow running without approval.
/// </summary>
/// <param name="AgentId">The agent.</param>
/// <param name="Applies">Whether the agent does work of its own, where a switch can apply: false for a
/// chat-only agent, a shadow variant and an agent without runs, whose tools then list without a way to switch.</param>
/// <param name="Tools">The tools, by id.</param>
public sealed record NhAssistantWithoutApprovalListDto(
    string AgentId,
    bool Applies,
    IReadOnlyList<NhAssistantWithoutApprovalSwitchDto> Tools);

/// <summary>
/// One version of a switch: what it was switched to, by whom and when.
/// </summary>
public sealed record NhAssistantWithoutApprovalVersionDto(
    int Version,
    string Mode,
    string ChangedBy,
    DateTimeOffset ChangedAt,
    string? Reason,
    string ContractHash);

internal sealed class NhAssistantWithoutApprovalService(
    NhAssistantAgentCatalog agents,
    NhAssistantRegistrationState registration,
    IEnumerable<INhAiToolCatalog> catalogs,
    NhAssistantWithoutApprovalStore store,
    NhAssistantAgentEvents events,
    TimeProvider? timeProvider = null) : INhAssistantWithoutApprovalSwitches
{
    private const int MaxActorIdLength = 256;

    /// <summary>
    /// The application's clock, which a switch's time follows as a decision's does; the audit keeps the system clock.
    /// </summary>
    private readonly TimeProvider _applicationClock = timeProvider ?? TimeProvider.System;

    public async Task<NhAssistantWithoutApprovalListDto?> GetAsync(string agentId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        var agent = await agents.FindAsync(agentId, includeDisabled: true, cancellationToken);
        if (agent is null)
        {
            return null;
        }

        var tools = await AllowedToolsAsync(agent, cancellationToken);
        var switches = (await store.GetCurrentAsync(agentId, cancellationToken))
            .ToDictionary(item => item.ToolId, StringComparer.Ordinal);
        return new NhAssistantWithoutApprovalListDto(
            agentId,
            DoesOwnWork(agentId),
            tools.Select(tool => ToDto(tool, switches.GetValueOrDefault(tool.Id))).ToArray());
    }

    public async Task<IReadOnlyList<NhAssistantWithoutApprovalVersionDto>> GetVersionsAsync(
        string agentId,
        string toolId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
        return (await store.GetVersionsAsync(agentId, toolId, cancellationToken))
            .Select(version => new NhAssistantWithoutApprovalVersionDto(
                version.Version,
                version.Mode,
                version.ChangedBy,
                version.ChangedAt,
                version.Reason,
                version.ContractHash))
            .ToArray();
    }

    public async Task<TaskResult<NhAssistantWithoutApprovalSwitchDto>> SetAsync(
        string agentId,
        string toolId,
        NhAssistantWithoutApprovalRequest request,
        string actorId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        if (actorId.Length > MaxActorIdLength)
        {
            throw new ArgumentOutOfRangeException(nameof(actorId), $"An actor id has at most {MaxActorIdLength} characters.");
        }

        if (request.Mode is null)
        {
            return TaskResult<NhAssistantWithoutApprovalSwitchDto>.Failed(NhAssistantValidationErrors.Failed("mode", NhAssistantFieldErrors.Required));
        }
        if (!NhAssistantWithoutApprovalModes.IsKnown(request.Mode))
        {
            return TaskResult<NhAssistantWithoutApprovalSwitchDto>.Failed(NhAssistantValidationErrors.Failed("mode", NhAssistantFieldErrors.Invalid));
        }
        var reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
        if (reason?.Length > AssistantAgentToolSwitch.MaxReasonLength)
        {
            return TaskResult<NhAssistantWithoutApprovalSwitchDto>.Failed(NhAssistantValidationErrors.Failed("reason", NhAssistantFieldErrors.TooLong));
        }

        var agent = await agents.FindAsync(agentId, includeDisabled: true, cancellationToken);
        if (agent is null)
        {
            return TaskResult<NhAssistantWithoutApprovalSwitchDto>.Failed(NhAssistantErrorCodes.AgentNotFound, "The agent was not found.");
        }
        var tool = (await AllowedToolsAsync(agent, cancellationToken))
            .FirstOrDefault(item => string.Equals(item.Id, toolId, StringComparison.Ordinal));
        if (tool is null || !DoesOwnWork(agentId))
        {
            // The first key is missing: the tool does not allow it in code or the agent cannot use it,
            // or the agent never does work of its own.
            return TaskResult<NhAssistantWithoutApprovalSwitchDto>.Failed(
                NhAssistantErrorCodes.WithoutApprovalNotAllowed,
                "The tool does not allow running without approval for this agent.");
        }

        var current = await store.FindCurrentAsync(agentId, toolId, cancellationToken);
        if ((request.ExpectedVersion ?? 0) != (current?.Version ?? 0))
        {
            return TaskResult<NhAssistantWithoutApprovalSwitchDto>.Failed(
                NhAssistantAdminErrorCodes.VersionConflict,
                "The switch changed since it was read.");
        }

        var sameContract = current is not null && string.Equals(current.ContractHash, tool.ContractHash, StringComparison.Ordinal);
        if (current is not null && sameContract && current.Mode == request.Mode)
        {
            return TaskResult<NhAssistantWithoutApprovalSwitchDto>.Succeeded(ToDto(tool, current));
        }

        var inEffect = current is not null && sameContract ? current.Mode : NhAssistantWithoutApprovalModes.Off;
        if (request.Mode == NhAssistantWithoutApprovalModes.Live && inEffect != NhAssistantWithoutApprovalModes.Shadow)
        {
            return TaskResult<NhAssistantWithoutApprovalSwitchDto>.Failed(
                NhAssistantErrorCodes.WithoutApprovalShadowFirst,
                "Switch the tool to shadow first; it goes live only from shadow.");
        }

        var next = new AssistantAgentToolSwitch
        {
            Id = Guid.NewGuid(),
            AgentId = agentId,
            ToolId = toolId,
            Version = (current?.Version ?? 0) + 1,
            Mode = request.Mode,
            ContractHash = tool.ContractHash,
            ChangedBy = actorId,
            ChangedAt = _applicationClock.GetUtcNow(),
            Reason = reason
        };
        if (!await store.AddAsync(next, cancellationToken))
        {
            return TaskResult<NhAssistantWithoutApprovalSwitchDto>.Failed(
                NhAssistantAdminErrorCodes.VersionConflict,
                "The switch changed since it was read.");
        }

        await events.AgentToolWithoutApprovalChangedAsync(agentId, toolId, tool.Version, actorId, next.Mode, next.Version, cancellationToken);
        return TaskResult<NhAssistantWithoutApprovalSwitchDto>.Succeeded(ToDto(tool, next));
    }

    /// <summary>
    /// Whether the agent does work of its own, where a switch can apply.
    /// </summary>
    private bool DoesOwnWork(string agentId)
    {
        return registration.AgentRuns.TryGetValue(agentId, out var options)
            && !options.ChatOnly
            && options.ShadowOfAgentId is null;
    }

    /// <summary>
    /// The tools within the agent's tool selectors whose contract allows running without approval, at
    /// their highest version.
    /// </summary>
    private async Task<IReadOnlyList<NhAiToolDescriptor>> AllowedToolsAsync(NhAssistantAgent agent, CancellationToken cancellationToken)
    {
        var selectors = agent.Definition.ToolSelectors.Select(selector => new NhAiAgentGrant(selector)).ToArray();
        var tools = new List<NhAiToolDescriptor>();
        foreach (var listed in catalogs)
        {
            if (listed is INhAiChangingToolCatalog changing)
            {
                await changing.RefreshAsync(cancellationToken);
            }

            tools.AddRange(NhAiToolCatalogViews.Of(listed).Descriptors
                .Where(descriptor => NhAiWithoutApproval.IsAllowedBy(descriptor)
                    && selectors.Any(selector => selector.Matches(descriptor.Id))));
        }

        return tools
            .GroupBy(descriptor => descriptor.Id, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(descriptor => descriptor.Version).First())
            .OrderBy(descriptor => descriptor.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static NhAssistantWithoutApprovalSwitchDto ToDto(NhAiToolDescriptor tool, AssistantAgentToolSwitch? current)
    {
        var contractChanged = current is not null
            && !string.Equals(current.ContractHash, tool.ContractHash, StringComparison.Ordinal);
        return new NhAssistantWithoutApprovalSwitchDto(
            tool.Id,
            tool.Description,
            tool.Effect == NhAiToolEffect.Mutation ? "mutation" : "idempotent-mutation",
            current is null || contractChanged ? NhAssistantWithoutApprovalModes.Off : current.Mode,
            current?.Version ?? 0,
            contractChanged,
            current?.ChangedBy,
            current?.ChangedAt,
            current?.Reason);
    }
}
