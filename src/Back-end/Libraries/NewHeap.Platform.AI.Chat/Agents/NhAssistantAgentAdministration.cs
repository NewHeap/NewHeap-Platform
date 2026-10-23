using System.Text.Json;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// Administrator input for an agent. It carries no state: pause, stop, switching off and resume
/// change that through the agent control, never the definition; only a new agent created in the
/// administration may start off with <paramref name="StartsOff"/>. <paramref name="OfferedInChat"/>,
/// <paramref name="WorksOn"/>, <paramref name="ProfileName"/> and <paramref name="DataClassification"/>
/// apply to agents created in the administration; <see langword="null"/> keeps them, a new agent is
/// offered in the chat, works on all object types, uses the application's chat profile and has no
/// classification of its own, so its model calls are <see cref="NhAiDataClassification.Internal"/>.
/// Agents in code take them from their code. <paramref name="Metadata"/> sets values of agent
/// metadata entries of an agent created in the administration by name, a JSON <c>null</c> removing
/// one, with the same version as the definition; entries left out keep their values. An agent in code
/// changes its editable entries one by one.
/// </summary>
internal sealed record NhAssistantAgentInput(
    string Id,
    string DisplayName,
    string Description,
    string Instructions,
    IReadOnlyList<string> ToolSelectors,
    IReadOnlyList<string> McpServerIds,
    string? RequiredPolicy,
    NhAiAutonomyLevel Autonomy,
    bool? OfferedInChat = null,
    NhAssistantAgentWorksOn? WorksOn = null,
    bool StartsOff = false,
    string? ProfileName = null,
    string? DataClassification = null,
    IReadOnlyDictionary<string, JsonElement>? Metadata = null);

/// <summary>
/// Creates, overrides, resets and deletes agents. Every change is versioned and reported to the
/// business audit sinks as a content-free event.
/// </summary>
internal sealed class NhAssistantAgentAdministration(
    NhAssistantAdminStore store,
    NhAssistantAgentCatalog catalog,
    NhAssistantAgentRegistry registry,
    NhAssistantRegistrationState state,
    IEnumerable<INhAssistantBusinessAuditSink> auditSinks,
    NhAssistantRunConfigurationStore runConfigurationStore,
    NhAssistantRunConfigurations runConfigurations,
    NhAssistantWorksOnRule worksOnRule,
    Runs.NhAssistantSubjectAccessService subjects,
    NhAssistantRunConfigurationValidator validator,
    NhAssistantAgentMetadataService metadata)
{
    public const int MaxInstructionsLength = 20_000;

    private readonly IReadOnlyList<INhAssistantBusinessAuditSink> _auditSinks = auditSinks.ToArray();

    public async Task<TaskResult<NhAssistantAgent>> CreateAsync(
        NhAssistantAgentInput input,
        string actorId,
        CancellationToken cancellationToken)
    {
        var (validation, values) = await ValidateAsync(input, cancellationToken);
        if (!validation.Success)
        {
            return TaskResult<NhAssistantAgent>.Failed(validation);
        }
        await catalog.ListAsync(includeDisabled: true, cancellationToken);
        var profileName = input.ProfileName ?? DefaultProfileName();
        if (profileName is null)
        {
            return TaskResult<NhAssistantAgent>.Failed(
                NhAssistantAdminErrorCodes.ValidationFailed,
                "Administrator agents require UseChatProfile.");
        }

        var now = DateTimeOffset.UtcNow;
        // A new agent starts active, or off when its administrator chose so; its state changes
        // through the agent control only.
        var row = new AssistantAgent
        {
            Id = input.Id,
            Version = 1,
            Source = NhAssistantAgentSources.Admin,
            ProfileName = profileName,
            IsEnabled = true,
            RunState = input.StartsOff ? NhAssistantAgentRunStates.Off : NhAssistantAgentRunStates.Active,
            StateChangedAt = input.StartsOff ? now : null,
            StateChangedBy = input.StartsOff ? actorId : null,
            UpdatedAt = now,
            UpdatedBy = actorId
        };
        ApplyInput(row, input, 1);
        // The agent's metadata starts with its first version.
        var rows = NhAssistantAgentMetadataStore.NewRows(row.Id, values, actorId, now);
        if (!await store.AddAgentAsync(row, input.McpServerIds, cancellationToken, rows))
        {
            return Failed(NhAssistantAdminErrorCodes.AgentExists, "An agent with this id already exists.");
        }
        await AuditAsync(NhAssistantAuditEventKind.AdminAgentCreated, row.Id, actorId, cancellationToken);
        await metadata.AuditAsync(
            row.Id,
            rows.Select(item => new NhAssistantAgentMetadataChange(item.Name, NhAssistantAgentMetadataStore.Created, 1)).ToArray(),
            actorId,
            cancellationToken);
        return TaskResult<NhAssistantAgent>.Succeeded(catalog.ToAgent((await store.FindAgentAsync(row.Id, cancellationToken))!));
    }

    public async Task<TaskResult<NhAssistantAgent>> UpdateAsync(
        NhAssistantAgentInput input,
        int expectedVersion,
        string actorId,
        CancellationToken cancellationToken)
    {
        var (validation, values) = await ValidateAsync(input, cancellationToken);
        if (!validation.Success)
        {
            return TaskResult<NhAssistantAgent>.Failed(validation);
        }
        await catalog.ListAsync(includeDisabled: true, cancellationToken);
        var metadataChanges = new List<NhAssistantAgentMetadataChange>();
        var updated = await store.UpdateAgentAsync(
            input.Id,
            expectedVersion,
            row =>
            {
                ApplyInput(row, input, row.Version + 1);
                row.UpdatedAt = DateTimeOffset.UtcNow;
                row.UpdatedBy = actorId;
                if (row.Source == NhAssistantAgentSources.Code)
                {
                    row.IsOverridden = !MatchesCode(row, input.McpServerIds);
                    if (!row.IsOverridden && registry.TryGet(row.Id, out var code))
                    {
                        // Back to the exact code definition: keep the code asset identity.
                        NhAssistantAdminStore.ApplyCodeDefinition(row, NhAssistantAgentCatalog.ToCodeRow(code, row.UpdatedAt));
                    }
                }
            },
            input.McpServerIds,
            cancellationToken,
            values,
            metadataChanges);
        if (updated.Succeeded && updated.Value!.Source == NhAssistantAgentSources.Admin)
        {
            await RunConfigurationChangedAsync(cancellationToken);
        }

        var completed = await CompleteAsync(updated, NhAssistantAuditEventKind.AdminAgentUpdated, actorId, cancellationToken);
        if (completed.Success)
        {
            await metadata.AuditAsync(input.Id, metadataChanges, actorId, cancellationToken);
        }

        return completed;
    }

    /// <summary>
    /// The run configuration defined as data checks data skills against an administration agent's
    /// tool selectors and loses an agent that is deleted: every host reloads it.
    /// </summary>
    private async Task RunConfigurationChangedAsync(CancellationToken cancellationToken)
    {
        if (!runConfigurations.Enabled)
        {
            return;
        }

        await runConfigurationStore.TouchAsync(cancellationToken);
        await runConfigurations.ReloadAsync(cancellationToken);
    }

    /// <summary>
    /// Restores a code agent to its code definition and removes its MCP assignments.
    /// </summary>
    public async Task<TaskResult<NhAssistantAgent>> ResetAsync(
        string agentId,
        string actorId,
        CancellationToken cancellationToken)
    {
        await catalog.ListAsync(includeDisabled: true, cancellationToken);
        var existing = await store.FindAgentAsync(agentId, cancellationToken);
        if (existing is null)
        {
            return Failed(NhAssistantErrorCodes.AgentNotFound, "The agent was not found.");
        }
        if (existing.Source != NhAssistantAgentSources.Code || !registry.TryGet(agentId, out var code))
        {
            return Failed(NhAssistantAdminErrorCodes.NotCodeAgent, "Only code agents can be reset.");
        }

        var updated = await store.UpdateAgentAsync(
            agentId,
            null,
            row =>
            {
                row.UpdatedAt = DateTimeOffset.UtcNow;
                row.UpdatedBy = actorId;
                NhAssistantAdminStore.ApplyCodeDefinition(row, NhAssistantAgentCatalog.ToCodeRow(code, row.UpdatedAt));
                row.IsOverridden = false;
                // The run state stays as it is: a reset never resumes a paused or stopped agent.
            },
            [],
            cancellationToken);
        return await CompleteAsync(updated, NhAssistantAuditEventKind.AdminAgentReset, actorId, cancellationToken);
    }

    public async Task<TaskResult> DeleteAsync(string agentId, string actorId, CancellationToken cancellationToken)
    {
        await catalog.ListAsync(includeDisabled: true, cancellationToken);
        var outcome = await store.DeleteAdminAgentAsync(agentId, cancellationToken);
        if (outcome == NhAssistantStoreOutcome.NotFound)
        {
            return TaskResult.Failed(NhAssistantErrorCodes.AgentNotFound, "The agent was not found.");
        }
        if (outcome == NhAssistantStoreOutcome.Rejected)
        {
            return TaskResult.Failed(
                NhAssistantAdminErrorCodes.CodeAgentNotDeletable,
                "Code agents cannot be deleted; disable them instead.");
        }
        await AuditAsync(NhAssistantAuditEventKind.AdminAgentDeleted, agentId, actorId, cancellationToken);
        await RunConfigurationChangedAsync(cancellationToken);
        return TaskResult.Succeeded();
    }

    private async Task<TaskResult<NhAssistantAgent>> CompleteAsync(
        NhAssistantStoreResult<AssistantAgent> updated,
        NhAssistantAuditEventKind eventName,
        string actorId,
        CancellationToken cancellationToken)
    {
        if (updated.Outcome == NhAssistantStoreOutcome.NotFound)
        {
            return Failed(NhAssistantErrorCodes.AgentNotFound, "The agent was not found.");
        }
        if (updated.Outcome == NhAssistantStoreOutcome.Conflict)
        {
            return Failed(NhAssistantAdminErrorCodes.VersionConflict, "The agent was changed by someone else.");
        }
        await AuditAsync(eventName, updated.Value!.Id, actorId, cancellationToken);
        return TaskResult<NhAssistantAgent>.Succeeded(
            catalog.ToAgent((await store.FindAgentAsync(updated.Value.Id, cancellationToken))!));
    }

    private async Task<(TaskResult Result, IReadOnlyDictionary<string, NhAssistantAgentMetadataCheck?> Metadata)> ValidateAsync(
        NhAssistantAgentInput input,
        CancellationToken cancellationToken)
    {
        var errors = new NhAssistantValidationErrors()
            .Require(NhAssistantNames.IsSegment(input.Id), "id", NhAssistantFieldErrors.Invalid)
            .Require(!string.IsNullOrWhiteSpace(input.DisplayName), "displayName", NhAssistantFieldErrors.Required)
            .Require(input.DisplayName is not { Length: > 256 }, "displayName", NhAssistantFieldErrors.TooLong)
            .Require(!string.IsNullOrWhiteSpace(input.Description), "description", NhAssistantFieldErrors.Required)
            .Require(input.Description is not { Length: > 512 }, "description", NhAssistantFieldErrors.TooLong)
            .Require(!string.IsNullOrWhiteSpace(input.Instructions), "instructions", NhAssistantFieldErrors.Required)
            .Require(input.Instructions is not { Length: > MaxInstructionsLength }, "instructions", NhAssistantFieldErrors.TooLong)
            .Require(input.ToolSelectors.Count <= state.Limits.MaxToolSelectorsPerAgent, "toolSelectors", NhAssistantFieldErrors.TooLong)
            .Require(
                NhAssistantAgentDefinition.FitsSelectorStorage(input.ToolSelectors.Distinct(StringComparer.Ordinal)),
                "toolSelectors",
                NhAssistantFieldErrors.TooLong)
            .Require(input.ToolSelectors.All(NhAssistantAgentDefinition.IsValidSelector), "toolSelectors", NhAssistantFieldErrors.Invalid)
            .Require(input.McpServerIds.Count <= 32, "mcpServerIds", NhAssistantFieldErrors.TooLong)
            .Require(input.RequiredPolicy is not { Length: 0 or > 256 }, "requiredPolicy", NhAssistantFieldErrors.Invalid)
            .Require(Enum.IsDefined(input.Autonomy), "autonomy", NhAssistantFieldErrors.Invalid);
        // The input is for an agent created in the administration, new or existing; an agent in code
        // takes what it works on and its profile from its code, so they are not checked for it.
        var inCode = registry.TryGet(input.Id, out _);
        var stored = inCode ? null : await store.FindAgentAsync(input.Id, cancellationToken);
        var adminAgent = !inCode && (stored is null || stored.Source == NhAssistantAgentSources.Admin);
        if (input.WorksOn is { } worksOn && adminAgent)
        {
            worksOnRule.Validate(worksOn, subjects, errors);
        }
        // An agent created here uses a profile the application registered for the administration,
        // as a data skill does. The profile it has and the chat profile it gets by default stay
        // accepted, so an editor that sends the agent back unchanged is not refused. An agent in code
        // takes its profile from its code.
        if (input.ProfileName is { } profileName
            && adminAgent
            && !string.Equals(profileName, stored?.ProfileName, StringComparison.Ordinal)
            && !string.Equals(profileName, DefaultProfileName(), StringComparison.Ordinal))
        {
            errors.Require(validator.IsChoosableProfile(profileName), "profile", NhAssistantFieldErrors.NotFound);
        }
        // An agent created here classifies its model calls by name, as a data skill does.
        if (input.DataClassification is { } classification && adminAgent)
        {
            errors.Require(
                NhAssistantDataClassificationNames.TryParse(classification, out _),
                "dataClassification",
                NhAssistantFieldErrors.Invalid);
        }
        // The agent's metadata is checked as every save of one entry checks it (MD1).
        var values = metadata.CheckDefinition(
            input.Metadata ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal),
            inCode: !adminAgent,
            errors);
        if (!errors.IsEmpty)
        {
            return (errors.ToResult(), values);
        }
        var serverIds = input.McpServerIds.Distinct(StringComparer.Ordinal).ToArray();
        var existing = await store.FindExistingMcpServerIdsAsync(serverIds, cancellationToken);
        if (existing.Count != serverIds.Length)
        {
            return (TaskResult.Failed(NhAssistantAdminErrorCodes.McpServerNotFound, "An assigned MCP server does not exist."), values);
        }
        return (TaskResult.Succeeded(), values);
    }

    private static void ApplyInput(AssistantAgent row, NhAssistantAgentInput input, int assetVersion)
    {
        var instructions = input.Instructions.Replace("\r\n", "\n", StringComparison.Ordinal);
        row.DisplayName = input.DisplayName.Trim();
        row.Description = input.Description.Trim();
        row.ToolSelectorsJson = JsonSerializer.Serialize(input.ToolSelectors.Distinct(StringComparer.Ordinal).ToArray());
        row.RequiredPolicy = string.IsNullOrWhiteSpace(input.RequiredPolicy) ? null : input.RequiredPolicy;
        row.Autonomy = input.Autonomy;
        if (input.OfferedInChat is { } offered && row.Source == NhAssistantAgentSources.Admin)
        {
            row.HiddenFromChat = !offered;
        }
        if (input.WorksOn is { } worksOn && row.Source == NhAssistantAgentSources.Admin)
        {
            worksOn.ApplyTo(row);
        }
        if (input.ProfileName is { } profileName && row.Source == NhAssistantAgentSources.Admin)
        {
            row.ProfileName = profileName;
        }
        if (input.DataClassification is { } classification && row.Source == NhAssistantAgentSources.Admin)
        {
            row.DataClassification = classification;
        }
        var hash = NhAssistantAgentCatalog.ComputeHash(instructions);
        if (!string.Equals(hash, row.InstructionsHash, StringComparison.Ordinal))
        {
            row.Instructions = instructions;
            row.InstructionsHash = hash;
            row.InstructionsAssetId = row.Id + "-instructions";
            row.InstructionsAssetVersion = assetVersion;
        }
    }

    private bool MatchesCode(AssistantAgent row, IReadOnlyList<string> mcpServerIds)
    {
        if (!registry.TryGet(row.Id, out var code) || mcpServerIds.Count > 0)
        {
            return false;
        }
        var codeRow = NhAssistantAgentCatalog.ToCodeRow(code, row.UpdatedAt);
        return row.DisplayName == codeRow.DisplayName
            && row.Description == codeRow.Description
            && row.InstructionsHash == codeRow.InstructionsHash
            && row.ToolSelectorsJson == codeRow.ToolSelectorsJson
            && row.RequiredPolicy == codeRow.RequiredPolicy
            && row.Autonomy == codeRow.Autonomy;
    }

    private string? DefaultProfileName()
    {
        return state.ChatProfileName ?? registry.Agents.FirstOrDefault()?.ProfileName;
    }

    private async Task AuditAsync(NhAssistantAuditEventKind kind, string objectId, string actorId, CancellationToken cancellationToken)
    {
        await NhAssistantAdminAudit.RecordAsync(_auditSinks, kind, objectId, actorId, cancellationToken);
    }

    private static TaskResult<NhAssistantAgent> Failed(string code, string message)
    {
        return TaskResult<NhAssistantAgent>.Failed(code, message);
    }
}

/// <summary>
/// Writes content-free administration events: event name, object id and actor only.
/// </summary>
internal static class NhAssistantAdminAudit
{
    public static async Task RecordAsync(
        IReadOnlyList<INhAssistantBusinessAuditSink> sinks,
        NhAssistantAuditEventKind kind,
        string objectId,
        string actorId,
        CancellationToken cancellationToken)
    {
        var evt = new NhAssistantAuditEvent(
            kind,
            Guid.Empty,
            Guid.Empty,
            actorId,
            string.Empty,
            0,
            DateTimeOffset.UtcNow)
        {
            ObjectId = objectId
        };
        foreach (var sink in sinks)
        {
            await sink.RecordAsync(evt, cancellationToken);
        }
    }
}
