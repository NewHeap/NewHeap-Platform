using System.Globalization;
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
/// changes its editable entries one by one. <paramref name="InstructionsAsset"/> lets an agent created
/// in the administration take its instructions from an asset of the application instead of
/// <paramref name="Instructions"/>, which is then empty. <paramref name="StartsInShadow"/> lets a new
/// agent created in the administration start in shadow: its runs work in its shadow threads once it has
/// run settings, until an administrator puts it live.
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
    IReadOnlyDictionary<string, JsonElement>? Metadata = null,
    NhAssistantInstructionAssetReference? InstructionsAsset = null,
    bool StartsInShadow = false)
{
    /// <summary>
    /// For a new agent: the live agent it is a data shadow variant of, within
    /// <c>UseShadowVariantsAsData</c>. Set when the agent is created and never changed; an update
    /// ignores it. A variant always works in shadow, so it ignores <see cref="StartsInShadow"/>.
    /// </summary>
    public string? ShadowOf { get; init; }

    /// <summary>
    /// For an update: the agent keeps its MCP assignments and <see cref="McpServerIds"/>, which is then
    /// empty, is ignored, as <c>INhAssistantAgentDefinitions</c> does unless its request names them. The
    /// administration's endpoint replaces them.
    /// </summary>
    public bool KeepMcpServers { get; init; }
}

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
    NhAssistantAgentMetadataService metadata,
    NhAssistantInstructionAssets instructionAssets,
    NhAssistantPackageLockCheck locks,
    NhAssistantAgentMetadataStore metadataStore)
{
    public const int MaxInstructionsLength = 20_000;

    private readonly IReadOnlyList<INhAssistantBusinessAuditSink> _auditSinks = auditSinks.ToArray();

    public async Task<TaskResult<NhAssistantAgent>> CreateAsync(
        NhAssistantAgentInput input,
        string actorId,
        CancellationToken cancellationToken)
    {
        var (failure, row, rows) = await PrepareNewAgentAsync(input, actorId, checkShadowVariant: true, cancellationToken);
        if (failure is not null)
        {
            return TaskResult<NhAssistantAgent>.Failed(failure);
        }

        if (!await store.AddAgentAsync(row!, input.McpServerIds, cancellationToken, rows))
        {
            return Failed(NhAssistantAdminErrorCodes.AgentExists, "An agent with this id already exists.");
        }

        await AuditCreatedAsync(row!, rows, actorId, cancellationToken);
        if (row!.InstructionsFromAsset)
        {
            // Every host checks the asset of the new agent at its next load.
            await RunConfigurationChangedAsync(cancellationToken);
        }

        return TaskResult<NhAssistantAgent>.Succeeded(await catalog.ToAgentAsync((await store.FindAgentAsync(row.Id, cancellationToken))!, cancellationToken));
    }

    /// <summary>
    /// The audit of a new agent: its creation, its shadow when it starts in shadow, and every value of
    /// its metadata, as a save of the agent records them.
    /// </summary>
    internal async Task AuditCreatedAsync(
        AssistantAgent row,
        IReadOnlyList<AssistantAgentMetadata> metadataRows,
        string actorId,
        CancellationToken cancellationToken)
    {
        await AuditAsync(NhAssistantAuditEventKind.AdminAgentCreated, row, actorId, cancellationToken);
        if (row.ShadowOverride == true)
        {
            await AuditShadowAsync(row.Id, actorId, cancellationToken);
        }

        await metadata.AuditAsync(
            row.Id,
            metadataRows.Select(item => new NhAssistantAgentMetadataChange(item.Name, NhAssistantAgentMetadataStore.Created, 1)).ToArray(),
            actorId,
            cancellationToken);
    }

    /// <summary>
    /// The audit of a changed agent and the metadata the change stored, as an update records them.
    /// </summary>
    internal async Task AuditUpdatedAsync(
        AssistantAgent row,
        IReadOnlyList<NhAssistantAgentMetadataChange> metadataChanges,
        string actorId,
        CancellationToken cancellationToken)
    {
        await AuditAsync(NhAssistantAuditEventKind.AdminAgentUpdated, row, actorId, cancellationToken);
        await metadata.AuditAsync(row.Id, metadataChanges, actorId, cancellationToken);
    }

    /// <summary>
    /// The checks and the first row of a new agent created in the administration, with the rows of its
    /// metadata, as <see cref="CreateAsync"/> stores them. An import of a package checks a shadow variant
    /// against the whole package instead (<paramref name="checkShadowVariant"/>).
    /// </summary>
    internal async Task<(TaskResult? Failure, AssistantAgent? Row, IReadOnlyList<AssistantAgentMetadata> MetadataRows)> PrepareNewAgentAsync(
        NhAssistantAgentInput input,
        string actorId,
        bool checkShadowVariant,
        CancellationToken cancellationToken)
    {
        var (validation, values) = await ValidateAsync(input, cancellationToken);
        if (!validation.Success)
        {
            return (validation, null, []);
        }
        await catalog.ListAsync(includeDisabled: true, cancellationToken);
        var profileName = input.ProfileName ?? DefaultProfileName();
        if (profileName is null)
        {
            return (TaskResult.Failed(
                NhAssistantAdminErrorCodes.ValidationFailed,
                "Administrator agents require UseChatProfile."), null, []);
        }
        var shadowOf = string.IsNullOrWhiteSpace(input.ShadowOf) ? null : input.ShadowOf.Trim();
        if (checkShadowVariant && shadowOf is not null && await CheckShadowVariantAsync(input.Id, shadowOf, cancellationToken) is { } refused)
        {
            return (refused, null, []);
        }
        if (shadowOf is not null)
        {
            // A data shadow variant always works in shadow: it stores no shadow override.
            input = input with { StartsInShadow = false };
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
            // A data shadow variant works for administrators only: it is never offered in the chat.
            ShadowOfAgentId = shadowOf,
            HiddenFromChat = shadowOf is not null,
            // In shadow from its first row: the override applies as soon as run settings exist, also
            // when they are saved right after in the same request, and no trigger reaches it live first.
            ShadowOverride = input.StartsInShadow ? true : null,
            ShadowChangedAt = input.StartsInShadow ? now : null,
            ShadowChangedBy = input.StartsInShadow ? actorId : null,
            UpdatedAt = now,
            UpdatedBy = actorId
        };
        ApplyInput(row, input, 1);
        // The agent's metadata starts with its first version.
        var rows = NhAssistantAgentMetadataStore.NewRows(row.Id, values, actorId, now);
        return (null, row, rows);
    }

    /// <summary>
    /// The checks of a change of an agent's definition, with the checked values of its metadata, as
    /// <see cref="UpdateAsync"/> applies them; an import of a package writes them in its transaction.
    /// </summary>
    internal async Task<(TaskResult Result, IReadOnlyDictionary<string, NhAssistantAgentMetadataCheck?> Metadata)> PrepareChangeAsync(
        NhAssistantAgentInput input,
        CancellationToken cancellationToken)
    {
        return await ValidateAsync(input, cancellationToken);
    }

    /// <summary>
    /// Applies a change of an agent's definition to its row as <see cref="UpdateAsync"/> does, for an
    /// import of a package that writes it in its transaction; the store raises the version.
    /// </summary>
    internal void ApplyChange(AssistantAgent row, NhAssistantAgentInput input, string actorId)
    {
        ApplyInput(row, input, row.Version + 1);
        row.UpdatedAt = DateTimeOffset.UtcNow;
        row.UpdatedBy = actorId;
        if (row.Source == NhAssistantAgentSources.Code)
        {
            // The import keeps the agent's MCP assignments, which never travel.
            row.IsOverridden = !MatchesCode(row, row.McpServers.Select(server => server.McpServerId).ToArray());
            if (!row.IsOverridden && registry.TryGet(row.Id, out var code))
            {
                // Back to the exact code definition: keep the code asset identity.
                NhAssistantAdminStore.ApplyCodeDefinition(row, NhAssistantAgentCatalog.ToCodeRow(code, row.UpdatedAt));
            }
        }
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
        // A package's locks hold on every save of the definition and its metadata (PK9).
        if (await CheckLocksAsync(input, values, cancellationToken) is { } locked)
        {
            return TaskResult<NhAssistantAgent>.Failed(locked);
        }

        var metadataChanges = new List<NhAssistantAgentMetadataChange>();
        // Null keeps the agent's MCP assignments in the store.
        var mcpServerIds = input.KeepMcpServers ? null : input.McpServerIds;
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
                    row.IsOverridden = !MatchesCode(row, mcpServerIds ?? row.McpServers.Select(server => server.McpServerId).ToArray());
                    if (!row.IsOverridden && registry.TryGet(row.Id, out var code))
                    {
                        // Back to the exact code definition: keep the code asset identity.
                        NhAssistantAdminStore.ApplyCodeDefinition(row, NhAssistantAgentCatalog.ToCodeRow(code, row.UpdatedAt));
                    }
                }
            },
            mcpServerIds,
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
    /// The refusal of an update that changes a field an agent package locks on the definition, or a
    /// locked metadata entry the definition carries: <c>{field}: locked</c> for the definition,
    /// <c>metadata.{name}: locked</c> for an entry, and <c>409 assistant-package-locked</c> for an entry
    /// the update removes. <see langword="null"/> when nothing locked changes.
    /// </summary>
    private async Task<TaskResult?> CheckLocksAsync(
        NhAssistantAgentInput input,
        IReadOnlyDictionary<string, NhAssistantAgentMetadataCheck?> values,
        CancellationToken cancellationToken)
    {
        var links = await locks.LinksOfAsync(input.Id, cancellationToken);
        if (links.Count == 0)
        {
            return null;
        }

        var before = await store.FindAgentAsync(input.Id, cancellationToken);
        var after = await store.FindAgentAsync(input.Id, cancellationToken);
        if (before is null || after is null)
        {
            return null;
        }

        ApplyInput(after, input, after.Version + 1);
        var definitionLink = links.FirstOrDefault(link => link.PartKind == NhAssistantPackagePartKinds.Agent);
        if (definitionLink is not null
            && NhAssistantPackageLockCheck.Check(definitionLink, NhAssistantPackageFields.Of(before), NhAssistantPackageFields.Of(after)) is { } refused)
        {
            return refused;
        }

        var stored = values.Count == 0 ? [] : await metadataStore.ListAsync(input.Id, cancellationToken);
        var errors = new NhAssistantValidationErrors();
        foreach (var (name, value) in values.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            var link = links.FirstOrDefault(item => item.PartKind == NhAssistantPackagePartKinds.Metadata && item.PartKey == name);
            if (link is null)
            {
                continue;
            }
            if (value is null)
            {
                if (NhAssistantAgentPackageStore.LockedFieldsOf(link).Count > 0)
                {
                    return NhAssistantPackageLockCheck.Locked();
                }

                continue;
            }

            var current = stored.FirstOrDefault(row => row.Name == name)?.ValueJson;
            if (NhAssistantPackageLockCheck.Check(link, NhAssistantPackageFields.OfMetadata(current), NhAssistantPackageFields.OfMetadata(value.Json)) is not null)
            {
                errors.Add("metadata." + name, NhAssistantFieldErrors.Locked);
            }
        }

        return errors.IsEmpty ? null : errors.ToResult();
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

        // A reset that would give a field a package locks another value waits for an unlink.
        var reset = (await store.FindAgentAsync(agentId, cancellationToken))!;
        NhAssistantAdminStore.ApplyCodeDefinition(reset, NhAssistantAgentCatalog.ToCodeRow(code, DateTimeOffset.UtcNow));
        if (await locks.CheckResetAsync(agentId, NhAssistantPackageFields.Of(existing), NhAssistantPackageFields.Of(reset), cancellationToken) is { } locked)
        {
            return TaskResult<NhAssistantAgent>.Failed(locked);
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
        // An agent that belongs to a package stays until an administrator unlinks it (PK9).
        if (await locks.CheckDeletionAsync(agentId, cancellationToken) is { } locked)
        {
            return locked;
        }

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
        await NhAssistantAdminAudit.RecordAsync(_auditSinks, NhAssistantAuditEventKind.AdminAgentDeleted, agentId, actorId, cancellationToken);
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
        var row = updated.Value!;
        await AuditAsync(eventName, row, actorId, cancellationToken);
        return TaskResult<NhAssistantAgent>.Succeeded(
            await catalog.ToAgentAsync((await store.FindAgentAsync(row.Id, cancellationToken))!, cancellationToken));
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
            // The instructions are text or an asset of the application, never both.
            .Require(input.InstructionsAsset is not null || !string.IsNullOrWhiteSpace(input.Instructions), "instructions", NhAssistantFieldErrors.Required)
            .Require(input.InstructionsAsset is null || string.IsNullOrWhiteSpace(input.Instructions), "instructions", NhAssistantFieldErrors.NotAllowed)
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
        // takes its profile from its code. A profile it chooses must run in the execution region, itself
        // or through a fallback, or every model call of the agent would be refused (LS7); startup checks
        // the default chat profile.
        if (input.ProfileName is { } profileName
            && adminAgent
            && !string.Equals(profileName, stored?.ProfileName, StringComparison.Ordinal)
            && !string.Equals(profileName, DefaultProfileName(), StringComparison.Ordinal))
        {
            errors
                .Require(validator.IsChoosableProfile(profileName), "profile", NhAssistantFieldErrors.NotFound)
                .Require(validator.PermitsExecutionRegion(profileName), "profile", NhAssistantFieldErrors.RegionNotPermitted);
        }
        // An agent created here classifies its model calls by name, as a data skill does.
        if (input.DataClassification is { } classification && adminAgent)
        {
            errors.Require(
                NhAssistantDataClassificationNames.TryParse(classification, out _),
                "dataClassification",
                NhAssistantFieldErrors.Invalid);
        }
        // Only an agent created in the administration takes its instructions from an asset; the save
        // asks the application's source and refuses an asset that is missing or has another hash.
        if (input.InstructionsAsset is { } reference)
        {
            if (!adminAgent)
            {
                errors.Add(NhAssistantInstructionAssets.AssetField, NhAssistantFieldErrors.NotAllowed);
            }
            else if (await instructionAssets.ValidateForSaveAsync(reference, MaxInstructionsLength, errors, cancellationToken) is { } unavailable)
            {
                return (unavailable, new Dictionary<string, NhAssistantAgentMetadataCheck?>(StringComparer.Ordinal));
            }
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
            // A data shadow variant stays out of the chat.
            row.HiddenFromChat = !offered || row.ShadowOfAgentId is not null;
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
        if (input.InstructionsAsset is { } reference && row.Source == NhAssistantAgentSources.Admin)
        {
            // The reference is stored, never the text: every load asks the application's source.
            row.InstructionsFromAsset = true;
            row.Instructions = string.Empty;
            row.InstructionsAssetId = reference.AssetId;
            row.InstructionsAssetVersion = reference.Version;
            row.InstructionsHash = reference.Hash;
            return;
        }

        var hash = NhAssistantAgentCatalog.ComputeHash(instructions);
        if (row.InstructionsFromAsset || !string.Equals(hash, row.InstructionsHash, StringComparison.Ordinal))
        {
            row.InstructionsFromAsset = false;
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

    /// <summary>
    /// Why a new agent may not be a data shadow variant of <paramref name="liveAgentId"/>, or
    /// <see langword="null"/>: the checks of every load (<see cref="NhAssistantRunConfigurationValidator.CheckShadowVariant"/>)
    /// against the configuration in effect, and the ceiling's number of variants per live agent.
    /// </summary>
    private async Task<TaskResult?> CheckShadowVariantAsync(string variantId, string liveAgentId, CancellationToken cancellationToken)
    {
        await runConfigurations.EnsureCurrentAsync(cancellationToken);
        var problem = validator.CheckShadowVariant(variantId, liveAgentId, state.AgentRuns);
        switch (problem)
        {
            case NhAssistantRunConfigurationProblems.OutsideCeiling:
                return TaskResult.Failed(
                    NhAssistantErrorCodes.ShadowVariantsNotAllowed,
                    "The agent may not get shadow variants created as data.");
            case NhAssistantRunConfigurationProblems.UnknownLiveAgent:
                return NhAssistantValidationErrors.Failed("shadowOf", NhAssistantFieldErrors.NotFound);
            case not null:
                return NhAssistantValidationErrors.Failed("shadowOf", NhAssistantFieldErrors.Invalid);
        }

        var existing = await runConfigurationStore.ListShadowVariantsAsync(liveAgentId, cancellationToken);
        if (existing.Count >= state.ShadowVariantsAsData!.MaxPerAgent)
        {
            return TaskResult.Failed(
                NhAssistantErrorCodes.ConfigurationLimit,
                "The agent has as many shadow variants created as data as allowed.");
        }

        return null;
    }

    /// <summary>
    /// The content-free event of a change of the agent, with the identity of its instructions:
    /// <c>&lt;asset-id&gt;@&lt;version&gt;</c> and the hash, never the text; a data shadow variant that
    /// was created also names its live agent.
    /// </summary>
    private async Task AuditAsync(NhAssistantAuditEventKind kind, AssistantAgent row, string actorId, CancellationToken cancellationToken)
    {
        await NhAssistantAdminAudit.RecordAsync(
            _auditSinks,
            kind,
            row.Id,
            actorId,
            cancellationToken,
            row.InstructionsAssetId + "@" + row.InstructionsAssetVersion.ToString(CultureInfo.InvariantCulture),
            row.InstructionsHash,
            kind == NhAssistantAuditEventKind.AdminAgentCreated ? row.ShadowOfAgentId : null);
    }

    /// <summary>
    /// Records that a new agent starts in shadow, as an administrator's shadow change records it.
    /// </summary>
    private async Task AuditShadowAsync(string agentId, string actorId, CancellationToken cancellationToken)
    {
        var evt = new NhAssistantAuditEvent(
            NhAssistantAuditEventKind.AdminAgentShadowChanged,
            Guid.Empty,
            Guid.Empty,
            actorId,
            agentId,
            0,
            DateTimeOffset.UtcNow)
        {
            ObjectId = agentId,
            ResultCode = "shadow"
        };
        foreach (var sink in _auditSinks)
        {
            await sink.RecordAsync(evt, cancellationToken);
        }
    }

    private static TaskResult<NhAssistantAgent> Failed(string code, string message)
    {
        return TaskResult<NhAssistantAgent>.Failed(code, message);
    }
}

/// <summary>
/// Writes content-free administration events: event name, object id and actor only, and for a data
/// shadow variant that was created the live agent it is a variant of.
/// </summary>
internal static class NhAssistantAdminAudit
{
    /// <param name="instructionsVersion">For a change of an agent: the identity of its instructions,
    /// <c>&lt;asset-id&gt;@&lt;version&gt;</c>.</param>
    /// <param name="instructionsHash">For a change of an agent: the hash of its instructions.</param>
    public static async Task RecordAsync(
        IReadOnlyList<INhAssistantBusinessAuditSink> sinks,
        NhAssistantAuditEventKind kind,
        string objectId,
        string actorId,
        CancellationToken cancellationToken,
        string? instructionsVersion = null,
        string? instructionsHash = null,
        string? liveAgentId = null)
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
            ObjectId = objectId,
            InstructionsVersion = instructionsVersion,
            InstructionsHash = instructionsHash,
            LiveAgentId = liveAgentId
        };
        foreach (var sink in sinks)
        {
            await sink.RecordAsync(evt, cancellationToken);
        }
    }
}
