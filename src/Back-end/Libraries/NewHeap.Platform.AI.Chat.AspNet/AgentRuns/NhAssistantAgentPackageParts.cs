using System.Text.Json;
using NewHeap.Platform.AI.Chat.Entities;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// The parts of an agent package as the host stores them, and the requests an import makes of them
/// (PK3): a stored row reads into its part record for an export and for the hash that tells whether a
/// part changed locally, and a part record maps to the request a save takes, so the import goes
/// through the save's checks.
/// </summary>
internal static class NhAssistantAgentPackageParts
{
    /// <summary>
    /// The definition of an agent: whole for an agent created in the administration, the overrides of
    /// an agent in code, or <see langword="null"/> for an agent in code without overrides.
    /// </summary>
    public static NhAssistantPackageAgentDefinition? DefinitionOf(AssistantAgent row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var selectors = NhAssistantRunConfigurations.ReadSelectors(row.ToolSelectorsJson) ?? [];
        var autonomy = row.Autonomy.ToString().ToLowerInvariant();
        if (row.Source != NhAssistantAgentSources.Admin)
        {
            return row.IsOverridden
                ? new NhAssistantPackageAgentDefinition(
                    DisplayName: row.DisplayName,
                    Description: row.Description,
                    Instructions: row.Instructions,
                    ToolSelectors: selectors,
                    RequiredPolicy: row.RequiredPolicy,
                    Autonomy: autonomy)
                : null;
        }

        var worksOn = NhAssistantAgentWorksOn.FromRow(row);
        return new NhAssistantPackageAgentDefinition(
            DisplayName: row.DisplayName,
            Description: row.Description,
            Instructions: row.InstructionsFromAsset ? null : row.Instructions,
            InstructionsAsset: row.InstructionsFromAsset
                ? new NhAssistantInstructionAssetReference(row.InstructionsAssetId, row.InstructionsAssetVersion, row.InstructionsHash)
                : null,
            ToolSelectors: selectors,
            RequiredPolicy: row.RequiredPolicy,
            Autonomy: autonomy,
            OfferedInChat: !row.HiddenFromChat,
            WorksOnSubjectTypes: worksOn.SubjectTypes,
            WorksOnAllSubjects: worksOn.AllSubjects,
            WorksOnApplication: worksOn.Application,
            PerDivision: worksOn.PerDivision,
            RunPolicy: worksOn.RunPolicy,
            ViewPolicy: worksOn.ViewPolicy,
            Profile: row.ProfileName,
            DataClassification: row.DataClassification,
            ShadowOf: row.ShadowOfAgentId)
        {
            // Written only when on, so the hash of an agent without them is the one it had before they
            // existed and no linked agent reads as changed locally.
            Compositions = row.UsesCompositions ? true : null,
            Scripts = row.UsesScripts ? true : null
        };
    }

    public static NhAssistantPackageRunSettings RunSettingsOf(AssistantAgentRunConfiguration row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new NhAssistantPackageRunSettings(
            row.Accountability,
            row.DelegateFromInitiator,
            row.AllowGatewayReads,
            row.ElevatedReads,
            row.ApprovalPolicyId,
            row.RetryCount,
            row.DailyToolCalls,
            row.DailyTokens,
            row.MaxConcurrentRuns,
            row.MaxRunsPerSubjectPerDay,
            row.MaxTokensPerRun,
            row.TriageProfileName,
            row.RuleHandlerName,
            row.AcceptsFiles,
            row.MaxFileBytes,
            row.MaxFilePages,
            row.MaxFilesPerTurn,
            row.MaxFileTokensPerTurn);
    }

    public static NhAssistantPackageSkill SkillOf(AssistantAgentSkillDefinition row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var asset = NhAssistantInstructionAssets.ReferenceOf(row);
        return new NhAssistantPackageSkill(
            row.SkillId,
            row.Description,
            asset is null ? row.Instructions : null,
            asset,
            NhAssistantRunConfigurations.ReadSelectors(row.ToolSelectorsJson) ?? [],
            row.ProfileName,
            row.RuleHandlerName,
            row.MaxTokensPerRun,
            row.DailyToolCalls,
            row.DailyTokens,
            row.ApprovalPolicyId,
            row.DataClassification,
            row.AcceptsFiles,
            row.MaxFileBytes,
            row.MaxFilePages,
            row.MaxFilesPerTurn,
            row.MaxFileTokensPerTurn);
    }

    public static NhAssistantPackageTrigger TriggerOf(AssistantAgentTriggerDefinition row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var schedule = row.Source == NhAssistantDataTriggerSources.Schedule;
        var filters = NhAssistantDataTriggerFilters.Read(row.FiltersJson) ?? [];
        return new NhAssistantPackageTrigger(
            row.Id,
            row.Source,
            row.SkillId,
            row.EventName,
            filters.Count == 0 ? null : filters,
            row.Cron,
            row.TimeZone,
            row.SubjectSetName,
            row.Message,
            row.Conflict,
            schedule ? null : row.DebounceSeconds);
    }

    public static NhAssistantPackageConsultation ConsultationOf(AssistantAgentConsultationDefinition row)
    {
        ArgumentNullException.ThrowIfNull(row);
        NhAssistantDataConsultationToolSelectors.TryRead(row.ToolSelectorsJson, out var selectors);
        return new NhAssistantPackageConsultation(
            row.SubjectType,
            row.Description,
            row.Instructions,
            row.MinimumRole,
            row.AskerPolicy,
            NhAssistantDataConsultationRoutes.Read(row.RoutesJson) ?? [],
            row.ProposalRole,
            row.ProposalPolicyId,
            selectors,
            row.MaxToolCalls,
            row.TimeoutSeconds,
            row.MaxAnswerChars,
            row.MaxHistoryMessages,
            row.AllowRequests,
            row.ExposeToMcp,
            row.DataClassification);
    }

    public static NhAssistantPackageHandOff HandOffOf(AssistantAgentHandOffDefinition row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new NhAssistantPackageHandOff(
            row.SubjectType,
            NhAssistantDataHandOffSources.Read(row.SourcesJson) ?? [],
            row.Description,
            row.NotifySource,
            row.MaxPerSubjectPerDay);
    }

    /// <summary>
    /// A stored value of agent metadata as the package carries it.
    /// </summary>
    public static JsonElement MetadataOf(string valueJson)
    {
        return NhAssistantAgentMetadataService.Parse(valueJson);
    }

    /// <summary>
    /// The definition of an agent created in the administration as the input a create or a change of
    /// the agent takes, with its metadata.
    /// </summary>
    public static NhAssistantAgentInput AdminInputOf(
        string agentId,
        NhAssistantPackageAgentDefinition definition,
        IReadOnlyDictionary<string, JsonElement>? metadata,
        bool autonomyValid,
        NhAiAutonomyLevel autonomy)
    {
        return new NhAssistantAgentInput(
            agentId,
            definition.DisplayName ?? string.Empty,
            definition.Description ?? string.Empty,
            definition.Instructions ?? string.Empty,
            definition.ToolSelectors ?? [],
            [],
            string.IsNullOrWhiteSpace(definition.RequiredPolicy) ? null : definition.RequiredPolicy,
            autonomyValid ? autonomy : (NhAiAutonomyLevel)(-1),
            definition.OfferedInChat ?? true,
            NhAssistantAgentWorksOn.FromInput(
                definition.WorksOnSubjectTypes,
                definition.WorksOnAllSubjects,
                definition.WorksOnApplication,
                definition.PerDivision,
                definition.RunPolicy,
                definition.ViewPolicy),
            definition.StartsOff == true,
            string.IsNullOrWhiteSpace(definition.Profile) ? null : definition.Profile.Trim(),
            string.IsNullOrWhiteSpace(definition.DataClassification) ? null : definition.DataClassification.Trim(),
            metadata,
            definition.InstructionsAsset,
            definition.StartsInShadow == true)
        {
            ShadowOf = definition.ShadowOf,
            // A package carries the features only when they are on: left out is off.
            Compositions = definition.Compositions == true,
            Scripts = definition.Scripts == true
        };
    }

    /// <summary>
    /// The overrides of an agent in code as the input a change of the agent takes: what the package
    /// leaves out keeps the value the agent has.
    /// </summary>
    public static NhAssistantAgentInput CodeInputOf(
        AssistantAgent row,
        NhAssistantPackageAgentDefinition definition,
        bool autonomyValid,
        NhAiAutonomyLevel autonomy)
    {
        return new NhAssistantAgentInput(
            row.Id,
            definition.DisplayName ?? row.DisplayName,
            definition.Description ?? row.Description,
            definition.Instructions ?? row.Instructions,
            definition.ToolSelectors ?? NhAssistantRunConfigurations.ReadSelectors(row.ToolSelectorsJson) ?? [],
            row.McpServers.Select(server => server.McpServerId).ToArray(),
            definition.RequiredPolicy is null ? row.RequiredPolicy : string.IsNullOrWhiteSpace(definition.RequiredPolicy) ? null : definition.RequiredPolicy,
            definition.Autonomy is null ? row.Autonomy : autonomyValid ? autonomy : (NhAiAutonomyLevel)(-1));
    }

    /// <summary>
    /// Reads an autonomy level written in lowercase, as the administration sends it.
    /// </summary>
    public static bool TryReadAutonomy(string? value, out NhAiAutonomyLevel autonomy)
    {
        autonomy = default;
        return value is not null
               && !value.Any(char.IsUpper)
               && Enum.TryParse(value, ignoreCase: true, out autonomy)
               && Enum.IsDefined(autonomy)
               && string.Equals(value, autonomy.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    public static NhAssistantAgentRunSettingsRequest RequestOf(
        NhAssistantPackageRunSettings part,
        string? serviceAccountActorId,
        int? expectedVersion,
        string reason)
    {
        return new NhAssistantAgentRunSettingsRequest(
            part.Accountability,
            serviceAccountActorId,
            part.DelegateFromInitiator,
            part.AllowGatewayReads,
            part.ElevatedReads,
            part.ApprovalPolicy,
            part.RetryCount,
            part.DailyToolCalls,
            part.DailyTokens,
            part.MaxConcurrentRuns,
            part.MaxRunsPerSubjectPerDay,
            part.MaxTokensPerRun,
            part.TriageProfile,
            part.RuleHandler,
            expectedVersion,
            reason,
            part.AcceptsFiles,
            part.MaxFileBytes,
            part.MaxFilePages,
            part.MaxFilesPerTurn,
            part.MaxFileTokensPerTurn);
    }

    public static NhAssistantDataSkillRequest RequestOf(
        NhAssistantPackageSkill part,
        int? expectedVersion,
        bool asCandidate,
        string reason)
    {
        return new NhAssistantDataSkillRequest(
            part.Description,
            part.Instructions,
            part.Tools,
            part.Profile,
            part.RuleHandler,
            part.MaxTokensPerRun,
            part.DailyToolCalls,
            part.DailyTokens,
            part.ApprovalPolicy,
            expectedVersion,
            reason,
            asCandidate ? NhAssistantSkillApplyModes.Shadow : null,
            expectedVersion is null && part.StartsOff == true,
            part.DataClassification,
            part.AcceptsFiles,
            part.MaxFileBytes,
            part.MaxFilePages,
            part.MaxFilesPerTurn,
            part.MaxFileTokensPerTurn)
        {
            InstructionsAsset = part.InstructionsAsset
        };
    }

    public static NhAssistantDataTriggerRequest RequestOf(
        NhAssistantPackageTrigger part,
        int? expectedVersion,
        bool asCandidate,
        string reason)
    {
        return new NhAssistantDataTriggerRequest(
            part.Source,
            part.Skill,
            part.Event,
            part.Cron,
            part.TimeZone,
            part.SubjectSet,
            part.Message,
            part.Conflict,
            part.DebounceSeconds,
            expectedVersion,
            reason,
            part.Filters)
        {
            Apply = asCandidate ? NhAssistantSkillApplyModes.Shadow : null
        };
    }

    public static NhAssistantDataConsultationRequest RequestOf(
        NhAssistantPackageConsultation part,
        int? expectedVersion,
        string reason)
    {
        return new NhAssistantDataConsultationRequest(
            part.Description,
            part.Instructions,
            part.MinimumRole,
            part.AskerPolicy,
            part.Routes,
            part.ProposalRole,
            part.ProposalPolicy,
            part.MaxToolCalls,
            part.TimeoutSeconds,
            part.MaxAnswerChars,
            part.MaxHistoryMessages,
            part.AllowRequests,
            part.ExposeToMcp,
            part.DataClassification,
            expectedVersion,
            reason)
        {
            ToolSelectors = part.ToolSelectors,
            StartsOff = expectedVersion is null && part.StartsOff == true
        };
    }

    public static NhAssistantDataHandOffRequest RequestOf(
        NhAssistantPackageHandOff part,
        int? expectedVersion,
        string reason)
    {
        return new NhAssistantDataHandOffRequest(
            part.Sources,
            part.Description,
            part.NotifySource,
            part.MaxPerSubjectPerDay,
            expectedVersion,
            reason);
    }
}
