using NewHeap.Platform.AI.Chat.Alerts;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet;

/// <summary>
/// The administrator's key of the model tool of alerts (FB87-9): lets the language model of one agent
/// raise alerts of one kind through the platform tool <c>assistant.alert.raise</c>, once the kind allows
/// it in code (<see cref="NhAssistantAlertKindBuilder.RaisedByModel"/>) and the agent lists it with
/// <c>Alerts(...)</c>. A switch goes from off to shadow, where the model's alerts are recorded as shadow
/// alerts that reach nobody and count as "would have sent" in the kind's numbers, and only from shadow to
/// live, where they reach the people the kind chooses, checked as every alert's recipients are. Every
/// change is the next version of the switch, stored with who made it, and audited as
/// <see cref="NhAssistantAuditEventKind.AdminAlertModelSwitchChanged"/>; switching off, or back to shadow,
/// applies to the next run on every host. A switch applies to the template it was made for: a kind whose
/// template changed reads as off until it is switched again, first in shadow.
/// <c>GET admin/agents/{id}/alert-model</c>, <c>PUT admin/agents/{id}/alert-kinds/{kindId}/model</c> and
/// <c>GET .../model/versions</c> use it behind the administration policy.
/// </summary>
public interface INhAssistantAlertModelSwitches
{
    /// <summary>
    /// The kinds the agent lists that allow a model, with their switches; <see langword="null"/> when the
    /// agent does not exist.
    /// </summary>
    Task<NhAssistantAlertModelListDto?> GetAsync(string agentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every version of the agent's switch for the kind, newest first; empty when nobody switched it.
    /// </summary>
    Task<IReadOnlyList<NhAssistantAlertModelVersionDto>> GetVersionsAsync(
        string agentId,
        string kindId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Switches the kind for the agent's model to <see cref="NhAssistantAlertModelRequest.Mode"/> when
    /// <see cref="NhAssistantAlertModelRequest.ExpectedVersion"/> is the current version (0 or
    /// <see langword="null"/> before the first switch). Fails with <c>assistant-agent-not-found</c>,
    /// <c>assistant-alert-kind-unknown</c>, <c>assistant-alert-model-not-allowed</c> (the kind does not allow
    /// a model in code, the agent does not list it, or the agent does no work of its own),
    /// <c>assistant-alert-model-shadow-first</c> (live from anything but shadow), <c>assistant-version-conflict</c>,
    /// or field errors for <c>mode</c> and <c>reason</c>. The same mode again changes nothing.
    /// </summary>
    /// <param name="actorId">Who switches: a person's actor id or the application's own actor, at most
    /// 256 characters.</param>
    Task<TaskResult<NhAssistantAlertModelSwitchDto>> SetAsync(
        string agentId,
        string kindId,
        NhAssistantAlertModelRequest request,
        string actorId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A change of the switch that lets an agent's model raise alerts of a kind.
/// </summary>
/// <param name="Mode"><c>off</c>, <c>shadow</c> or <c>live</c> (<see cref="NhAssistantAlertModelModes"/>).</param>
/// <param name="ExpectedVersion">The switch's current version, 0 or <see langword="null"/> before the first switch.</param>
/// <param name="Reason">Why, at most 500 characters; kept with the version and left out of the business audit.</param>
public sealed record NhAssistantAlertModelRequest(
    string? Mode,
    int? ExpectedVersion,
    string? Reason = null);

/// <summary>
/// One parameter of a kind's template for the model, as the administration shows it.
/// </summary>
/// <param name="Name">The parameter's name.</param>
/// <param name="Type"><c>choice</c>, <c>whole-number</c>, <c>date</c> or <c>text</c>.</param>
/// <param name="Description">What the model writes in it.</param>
/// <param name="Choices">The values of a choice; empty otherwise.</param>
/// <param name="Minimum">The lowest whole number, inclusive.</param>
/// <param name="Maximum">The highest whole number, inclusive.</param>
/// <param name="MaxLength">The longest text, in characters.</param>
/// <param name="Key">Whether its value forms the alert's key.</param>
public sealed record NhAssistantAlertModelParameterDto(
    string Name,
    string Type,
    string Description,
    IReadOnlyList<string> Choices,
    int? Minimum,
    int? Maximum,
    int? MaxLength,
    bool Key);

/// <summary>
/// One kind an agent lists that allows a model, with the switch in effect.
/// </summary>
/// <param name="KindId">The kind.</param>
/// <param name="DisplayNameKey">The front-end translation key of the kind's name.</param>
/// <param name="Description">When the kind applies, as its template tells the model.</param>
/// <param name="Parameters">What the model fills, in order.</param>
/// <param name="Mode">The mode in effect: <c>off</c>, <c>shadow</c> or <c>live</c>; <c>off</c> while the
/// kind's template differs from the one the switch was made for.</param>
/// <param name="Version">The switch's version, 0 when nobody switched it; the next change expects it.</param>
/// <param name="ContractChanged">Whether the kind's template changed since the switch was made, which
/// makes it read as off until it is switched again, first in shadow.</param>
/// <param name="KindEnabled">Whether the kind is on; an administrator may switch it off for everyone
/// (<c>PUT admin/alert-kinds/{id}</c>), and then nothing of it is raised, by the model either.</param>
/// <param name="ChangedBy">Who switched it last.</param>
/// <param name="ChangedAt">When it was switched last.</param>
/// <param name="Reason">The reason given with the last switch.</param>
public sealed record NhAssistantAlertModelSwitchDto(
    string KindId,
    string DisplayNameKey,
    string Description,
    IReadOnlyList<NhAssistantAlertModelParameterDto> Parameters,
    string Mode,
    int Version,
    bool ContractChanged,
    bool KindEnabled,
    string? ChangedBy,
    DateTimeOffset? ChangedAt,
    string? Reason);

/// <summary>
/// The kinds an agent's model may be switched on for.
/// </summary>
/// <param name="AgentId">The agent.</param>
/// <param name="Applies">Whether the agent does work of its own, where a switch can apply: false for a
/// chat-only agent, a shadow variant and an agent without runs, whose kinds then list without a way to switch.</param>
/// <param name="Kinds">The kinds, by id.</param>
public sealed record NhAssistantAlertModelListDto(
    string AgentId,
    bool Applies,
    IReadOnlyList<NhAssistantAlertModelSwitchDto> Kinds);

/// <summary>
/// One version of a switch: what it was switched to, by whom and when.
/// </summary>
public sealed record NhAssistantAlertModelVersionDto(
    int Version,
    string Mode,
    string ChangedBy,
    DateTimeOffset ChangedAt,
    string? Reason,
    string ContractHash);

internal sealed class NhAssistantAlertModelSwitchService(
    NhAssistantAgentCatalog agents,
    NhAssistantRegistrationState registration,
    NhAssistantAlertModelSwitchStore store,
    NhAssistantAlertKindSettings kindSettings,
    NhAssistantAgentEvents events,
    TimeProvider? timeProvider = null) : INhAssistantAlertModelSwitches
{
    private const int MaxActorIdLength = 256;

    /// <summary>
    /// The application's clock, which a switch's time follows as a decision's does; the audit keeps the system clock.
    /// </summary>
    private readonly TimeProvider _applicationClock = timeProvider ?? TimeProvider.System;

    public async Task<NhAssistantAlertModelListDto?> GetAsync(string agentId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        var agent = await agents.FindAsync(agentId, includeDisabled: true, cancellationToken);
        if (agent is null)
        {
            return null;
        }

        var switches = (await store.GetCurrentAsync(agentId, cancellationToken))
            .ToDictionary(item => item.KindId, StringComparer.Ordinal);
        var kinds = new List<NhAssistantAlertModelSwitchDto>();
        foreach (var kind in AllowedKinds(agentId))
        {
            var enabled = (await kindSettings.GetAsync(kind, cancellationToken)).Enabled;
            kinds.Add(ToDto(kind, switches.GetValueOrDefault(kind.Id), enabled));
        }

        return new NhAssistantAlertModelListDto(agentId, NhAssistantAlertModelTool.DoesOwnWork(registration, agentId), kinds);
    }

    public async Task<IReadOnlyList<NhAssistantAlertModelVersionDto>> GetVersionsAsync(
        string agentId,
        string kindId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(kindId);
        return (await store.GetVersionsAsync(agentId, kindId, cancellationToken))
            .Select(version => new NhAssistantAlertModelVersionDto(
                version.Version,
                version.Mode,
                version.ChangedBy,
                version.ChangedAt,
                version.Reason,
                version.ContractHash))
            .ToArray();
    }

    public async Task<TaskResult<NhAssistantAlertModelSwitchDto>> SetAsync(
        string agentId,
        string kindId,
        NhAssistantAlertModelRequest request,
        string actorId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(kindId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        if (actorId.Length > MaxActorIdLength)
        {
            throw new ArgumentOutOfRangeException(nameof(actorId), $"An actor id has at most {MaxActorIdLength} characters.");
        }

        if (request.Mode is null)
        {
            return TaskResult<NhAssistantAlertModelSwitchDto>.Failed(NhAssistantValidationErrors.Failed("mode", NhAssistantFieldErrors.Required));
        }
        if (!NhAssistantAlertModelModes.IsKnown(request.Mode))
        {
            return TaskResult<NhAssistantAlertModelSwitchDto>.Failed(NhAssistantValidationErrors.Failed("mode", NhAssistantFieldErrors.Invalid));
        }
        var reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
        if (reason?.Length > AssistantAgentAlertModelSwitch.MaxReasonLength)
        {
            return TaskResult<NhAssistantAlertModelSwitchDto>.Failed(NhAssistantValidationErrors.Failed("reason", NhAssistantFieldErrors.TooLong));
        }

        if (await agents.FindAsync(agentId, includeDisabled: true, cancellationToken) is null)
        {
            return TaskResult<NhAssistantAlertModelSwitchDto>.Failed(NhAssistantErrorCodes.AgentNotFound, "The agent was not found.");
        }
        if (!registration.AlertKinds.ContainsKey(kindId))
        {
            return TaskResult<NhAssistantAlertModelSwitchDto>.Failed(NhAssistantAlertCodes.KindUnknown, "The alert kind is not registered.");
        }
        var kind = AllowedKinds(agentId).FirstOrDefault(item => string.Equals(item.Id, kindId, StringComparison.Ordinal));
        if (kind is null || !NhAssistantAlertModelTool.DoesOwnWork(registration, agentId))
        {
            // The first key is missing: the kind does not allow a model in code or the agent does not
            // list it, or the agent never does work of its own.
            return TaskResult<NhAssistantAlertModelSwitchDto>.Failed(
                NhAssistantAdminErrorCodes.AlertModelNotAllowed,
                "The agent's model cannot raise alerts of this kind.");
        }

        var current = await store.FindCurrentAsync(agentId, kindId, cancellationToken);
        if ((request.ExpectedVersion ?? 0) != (current?.Version ?? 0))
        {
            return TaskResult<NhAssistantAlertModelSwitchDto>.Failed(
                NhAssistantAdminErrorCodes.VersionConflict,
                "The switch changed since it was read.");
        }

        var enabled = (await kindSettings.GetAsync(kind, cancellationToken)).Enabled;
        var template = kind.ModelTemplate!;
        var sameContract = current is not null && string.Equals(current.ContractHash, template.ContractHash, StringComparison.Ordinal);
        if (current is not null && sameContract && current.Mode == request.Mode)
        {
            return TaskResult<NhAssistantAlertModelSwitchDto>.Succeeded(ToDto(kind, current, enabled));
        }

        var inEffect = NhAssistantAlertModelTool.ModeOf(kind, current);
        if (request.Mode == NhAssistantAlertModelModes.Live && inEffect != NhAssistantAlertModelModes.Shadow)
        {
            return TaskResult<NhAssistantAlertModelSwitchDto>.Failed(
                NhAssistantAdminErrorCodes.AlertModelShadowFirst,
                "Switch the kind to shadow first; the model's alerts go live only from shadow.");
        }

        var next = new AssistantAgentAlertModelSwitch
        {
            Id = Guid.NewGuid(),
            AgentId = agentId,
            KindId = kindId,
            Version = (current?.Version ?? 0) + 1,
            Mode = request.Mode,
            ContractHash = template.ContractHash,
            ChangedBy = actorId,
            ChangedAt = _applicationClock.GetUtcNow(),
            Reason = reason
        };
        if (!await store.AddAsync(next, cancellationToken))
        {
            return TaskResult<NhAssistantAlertModelSwitchDto>.Failed(
                NhAssistantAdminErrorCodes.VersionConflict,
                "The switch changed since it was read.");
        }

        await events.AlertModelSwitchChangedAsync(agentId, kindId, actorId, next.Mode, next.Version, cancellationToken);
        return TaskResult<NhAssistantAlertModelSwitchDto>.Succeeded(ToDto(kind, next, enabled));
    }

    /// <summary>
    /// The kinds the agent lists with <c>Alerts(...)</c> that allow a model in code, ordered by id.
    /// </summary>
    private IReadOnlyList<NhAssistantAlertKind> AllowedKinds(string agentId)
    {
        if (!registration.AgentRuns.TryGetValue(agentId, out var options))
        {
            return [];
        }

        return options.AlertKinds
            .Select(kindId => registration.AlertKinds.GetValueOrDefault(kindId))
            .OfType<NhAssistantAlertKind>()
            .Where(kind => kind.ModelTemplate is not null)
            .OrderBy(kind => kind.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static NhAssistantAlertModelSwitchDto ToDto(NhAssistantAlertKind kind, AssistantAgentAlertModelSwitch? current, bool enabled)
    {
        var template = kind.ModelTemplate!;
        var contractChanged = current is not null
            && !string.Equals(current.ContractHash, template.ContractHash, StringComparison.Ordinal);
        return new NhAssistantAlertModelSwitchDto(
            kind.Id,
            kind.DisplayNameKey,
            template.Description,
            template.Parameters.Select(parameter => new NhAssistantAlertModelParameterDto(
                    parameter.Name,
                    TypeName(parameter.Type),
                    parameter.Description,
                    parameter.Choices,
                    parameter.Minimum,
                    parameter.Maximum,
                    parameter.MaxLength,
                    template.KeyParameters.Contains(parameter.Name, StringComparer.Ordinal)))
                .ToArray(),
            NhAssistantAlertModelTool.ModeOf(kind, current),
            current?.Version ?? 0,
            contractChanged,
            enabled,
            current?.ChangedBy,
            current?.ChangedAt,
            current?.Reason);
    }

    private static string TypeName(NhAssistantAlertModelParameterType type)
    {
        return type switch
        {
            NhAssistantAlertModelParameterType.Choice => "choice",
            NhAssistantAlertModelParameterType.WholeNumber => "whole-number",
            NhAssistantAlertModelParameterType.Date => "date",
            _ => "text"
        };
    }
}
