using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentBuilder;

/// <summary>
/// One call of an agent builder tool: the person who chats with the builder, whose rights it uses, and the
/// call's checked input.
/// </summary>
internal sealed record NhAssistantAgentBuilderCall(string Person, NhAssistantAgentBuilderInput Input)
{
    /// <summary>
    /// The author of what the call saves in a rule script: the person, with the agent builder acting for them.
    /// </summary>
    public NhAssistantRuleScriptAuthor Author => new(Person) { AgentId = NhAssistantAgentBuilderIds.AgentId };
}

/// <summary>
/// Runs the agent builder's tools (RB-8) through the services the administration's endpoints use, so their
/// validation, versions, ceilings, package locks, four eyes and audit apply unchanged, with the person who
/// chats as the actor. A call works only in a person's own chat with the agent builder, never in an agent's
/// run, a consultation or outside a turn, and never on the agent builder itself. The invoker already
/// authorized the tool's policy for that person and, for a write, checked their approval.
/// </summary>
internal sealed class NhAssistantAgentBuilderToolHandler(
    IServiceProvider services,
    NhAssistantRegistrationState registration,
    ILogger<NhAssistantAgentBuilderToolHandler> logger)
{
    /// <summary>The most tools the catalog lists in one result.</summary>
    public const int MaxCatalogTools = 300;

    /// <summary>The longest description of a tool the catalog lists.</summary>
    public const int MaxToolDescriptionLength = 300;

    /// <summary>
    /// How results reach the model: camelCase, enums by name, without empty values.
    /// </summary>
    internal static readonly JsonSerializerOptions ResultOptions = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) }
    };

    private INhAssistantAgentConfiguration Configuration => services.GetRequiredService<INhAssistantAgentConfiguration>();

    private INhAssistantAgentDefinitions Definitions => services.GetRequiredService<INhAssistantAgentDefinitions>();

    private INhAssistantAgentDirectory Directory => services.GetRequiredService<INhAssistantAgentDirectory>();

    private INhAssistantAgentControl Control => services.GetRequiredService<INhAssistantAgentControl>();

    private INhAssistantRuleScripts RuleScripts => services.GetRequiredService<INhAssistantRuleScripts>();

    /// <summary>
    /// Runs one call: in a person's chat with the agent builder only, with input that the tool takes.
    /// </summary>
    public async Task<TaskResult<JsonElement>> HandleAsync(
        NhAssistantAgentBuilderTool tool,
        JsonElement value,
        NhAiInvocationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(context);
        if (PersonOf() is not { } person)
        {
            logger.LogWarning(
                "The agent builder's tool {ToolId} was refused outside a person's chat with the agent builder.",
                tool.Id);
            return Refused(NhAssistantAdminErrorCodes.Forbidden, "The agent builder's tools work only in a person's chat with the agent builder.");
        }

        var (input, unknown) = NhAssistantAgentBuilderInput.Read(value, tool.Input.Properties);
        if (input is null)
        {
            return Refused(NhAiToolFailureCodes.InputInvalid, $"The tool does not take '{unknown}'. Use only the properties its schema names.");
        }

        try
        {
            return await tool.Handle(this, new NhAssistantAgentBuilderCall(person, input), cancellationToken);
        }
        catch (NhAssistantAgentBuilderInputException wrong)
        {
            return Refused(NhAiToolFailureCodes.InputInvalid, $"'{wrong.Property}' has the wrong type. Send it as its schema says.");
        }
    }

    /// <summary>
    /// The person of the running turn when it is their own chat with the agent builder: not an agent's run
    /// or thread, a consultation or a shadow run; <see langword="null"/> otherwise.
    /// </summary>
    private static string? PersonOf()
    {
        var turn = NhAssistantExecutionScope.CurrentTurn;
        if (turn is null
            || !NhAssistantAgentBuilderIds.IsAgentBuilder(turn.Agent.Id)
            || turn.Consultation is not null
            || turn.RunId is not null
            || turn.ShadowRun
            || NhAiAgentInvocationScopes.Current is not null)
        {
            return null;
        }

        return turn.OwnerActorId;
    }

    // ----- Reads -----

    public async Task<TaskResult<JsonElement>> ReadCatalogAsync(NhAssistantAgentBuilderCall call, CancellationToken cancellationToken)
    {
        var query = call.Input.Text("query");
        var tools = new List<CatalogTool>();
        foreach (var listed in services.GetServices<INhAiToolCatalog>())
        {
            // A catalog that changes at runtime reads its source first, as the administration's list does.
            if (listed is INhAiChangingToolCatalog changing)
            {
                await changing.RefreshAsync(cancellationToken);
            }

            tools.AddRange(NhAiToolCatalogViews.Of(listed).Descriptors
                .Where(descriptor => (descriptor.Exposure & NhAiToolExposure.Agent) != 0)
                .Where(descriptor => !NhAssistantAgentBuilderIds.IsAgentBuilderTool(descriptor.Id))
                .Select(descriptor => new CatalogTool(
                    descriptor.Id,
                    EffectName(descriptor.Effect),
                    NhAssistantContent.Bound(descriptor.Description, MaxToolDescriptionLength),
                    descriptor.Subject?.Type)));
        }

        var matching = tools
            .DistinctBy(tool => tool.Id, StringComparer.Ordinal)
            .Where(tool => query is null
                || tool.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
                || tool.Description.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(tool => tool.Id, StringComparer.Ordinal)
            .ToArray();
        var run = Configuration.GetCatalog();
        var alertKinds = registration.AlertKinds.Values
            .OrderBy(kind => kind.Id, StringComparer.Ordinal)
            .Select(kind => new CatalogAlertKind(
                kind.Id,
                kind.DisplayNameKey,
                kind.Urgency.ToString().ToLowerInvariant(),
                kind.ModelTemplate?.Description))
            .ToArray();
        return Ok(new
        {
            tools = matching.Take(MaxCatalogTools).ToArray(),
            toolsLeftOut = Math.Max(0, matching.Length - MaxCatalogTools),
            run.TriggerEvents,
            run.SubjectSets,
            run.SubjectTypes,
            run.RuleHandlers,
            approvalPolicies = run.ApprovalPolicySummaries.Count > 0 ? (object)run.ApprovalPolicySummaries : run.ApprovalPolicies,
            run.Profiles,
            run.AgentPolicies,
            run.Divisions,
            run.CompositionCeiling,
            alertKinds,
            ruleScripts = RuleScriptCeilingOf()
        });
    }

    public async Task<TaskResult<JsonElement>> ListAgentsAsync(CancellationToken cancellationToken)
    {
        var agents = await Directory.ListAsync(null, cancellationToken);
        return Ok(new
        {
            agents = agents
                .Where(agent => !NhAssistantAgentBuilderIds.IsAgentBuilder(agent.AgentId))
                .Select(agent => new
                {
                    agent.AgentId,
                    agent.Source,
                    agent.Version,
                    agent.DisplayName,
                    agent.Description,
                    agent.Autonomy,
                    agent.RunState,
                    agent.ChatOnly,
                    agent.InShadow,
                    agent.ShadowOf,
                    agent.OfferedInChat
                })
                .ToArray()
        });
    }

    public async Task<TaskResult<JsonElement>> ReadAgentAsync(NhAssistantAgentBuilderCall call, CancellationToken cancellationToken)
    {
        if (TargetOf(call) is not { } agentId)
        {
            return TargetRefused(call);
        }
        if (await Directory.GetAsync(agentId, cancellationToken) is not { } agent)
        {
            return Refused(NhAssistantErrorCodes.AgentNotFound, "The agent was not found.");
        }

        var configuration = await Configuration.GetAsync(agentId, cancellationToken);
        var ruleScript = await RuleScripts.GetAsync(agentId, null, cancellationToken);
        return Ok(new
        {
            agent,
            configuration,
            ruleScript = ruleScript is null
                ? null
                : new
                {
                    ruleScript.Allowed,
                    ruleScript.Revision,
                    ruleScript.LiveVersion,
                    ruleScript.CandidateVersion,
                    ruleScript.CandidateShadow,
                    ruleScript.CodeRule
                }
        });
    }

    public async Task<TaskResult<JsonElement>> ReadRuleScriptAsync(NhAssistantAgentBuilderCall call, CancellationToken cancellationToken)
    {
        if (TargetOf(call) is not { } agentId)
        {
            return TargetRefused(call);
        }

        var skillId = call.Input.Text("skillId");
        var ruleScript = await RuleScripts.GetAsync(agentId, skillId, cancellationToken);
        if (ruleScript is null)
        {
            return Refused(NhAssistantErrorCodes.AgentNotFound, "The agent or skill was not found.");
        }
        if (!ruleScript.Allowed)
        {
            return Refused(NhAssistantRuleScriptCodes.NotAllowed, "The code does not let this agent have rule scripts.");
        }

        var catalog = await RuleScripts.GetCatalogAsync(agentId, skillId, call.Input.Texts("writes"), cancellationToken);
        if (!catalog.Success)
        {
            return From(catalog, NhAssistantRuleScriptCodes.NotAllowed);
        }

        var candidateDryRun = ruleScript.CandidateVersion is { } candidate
            ? await RuleScripts.GetDryRunAsync(agentId, skillId, candidate, cancellationToken)
            : null;
        return Ok(new
        {
            ruleScript.AgentId,
            ruleScript.SkillId,
            ruleScript.Revision,
            ruleScript.LiveVersion,
            ruleScript.CandidateVersion,
            ruleScript.CandidateShadow,
            ruleScript.CodeRule,
            ruleScript.Ceiling,
            // The live version and the candidate in full; the history by number and status.
            versions = ruleScript.Versions
                .Where(version => version.Version == ruleScript.LiveVersion || version.Version == ruleScript.CandidateVersion)
                .ToArray(),
            history = ruleScript.Versions
                .Where(version => version.Version != ruleScript.LiveVersion && version.Version != ruleScript.CandidateVersion)
                .Select(version => new { version.Version, version.Status, version.ScriptHash, version.CreatedAt })
                .ToArray(),
            candidateDryRun,
            catalog = catalog.Data
        });
    }

    // ----- Dry runs -----

    public async Task<TaskResult<JsonElement>> DryRunRuleScriptAsync(NhAssistantAgentBuilderCall call, CancellationToken cancellationToken)
    {
        if (TargetOf(call) is not { } agentId)
        {
            return TargetRefused(call);
        }
        if (call.Input.Int("version") is not { } version)
        {
            return Missing("version");
        }

        var subjects = (call.Input.Objects("subjects") ?? [])
            .Select(subject => new NhAssistantRuleScriptSubjectDto(
                subject.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String ? type.GetString()! : string.Empty,
                subject.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString()! : string.Empty))
            .ToArray();
        var dryRun = await RuleScripts.DryRunAsync(
            agentId,
            call.Input.Text("skillId"),
            version,
            new NhAssistantRuleScriptDryRunRequest(subjects),
            call.Author,
            cancellationToken);
        return dryRun.Success ? Ok(dryRun.Data!) : From(dryRun, NhAssistantRuleScriptCodes.SubjectsInvalid);
    }

    // ----- Writes, each after the administrator's approval -----

    public async Task<TaskResult<JsonElement>> CreateAgentAsync(NhAssistantAgentBuilderCall call, CancellationToken cancellationToken)
    {
        var input = call.Input;
        if (TargetOf(call) is not { } agentId)
        {
            return TargetRefused(call);
        }
        if (Required(input, "displayName", "description", "instructions", "toolSelectors") is { } missing)
        {
            return missing;
        }
        if (AutonomyOf(input) is { Refused: { } wrongAutonomy })
        {
            return wrongAutonomy;
        }

        var request = new NhAssistantAgentRequest(
            agentId,
            input.Text("displayName")!,
            input.Text("description")!,
            input.RawText("instructions")!,
            input.Texts("toolSelectors")!,
            AutonomyOf(input).Value ?? NhAiAutonomyLevel.Propose,
            input.Text("requiredPolicy"),
            input.Bool("offeredInChat") ?? true,
            input.Texts("worksOnSubjectTypes"),
            input.Bool("worksOnAllSubjects"),
            input.Bool("worksOnApplication"),
            Profile: input.Text("profile"),
            DataClassification: input.Text("dataClassification"))
        {
            StartsInShadow = input.Bool("startsInShadow") ?? false
        };
        var created = await Configuration.CreateAgentAsync(request, call.Person, cancellationToken);
        return created.Success
            // The agent's id comes first, so the administration can follow the new agent to its page.
            ? Ok(new { agentId = created.Data!.AgentId, created = true, configuration = created.Data })
            : From(created, NhAssistantAdminErrorCodes.ValidationFailed);
    }

    public async Task<TaskResult<JsonElement>> UpdateAgentAsync(NhAssistantAgentBuilderCall call, CancellationToken cancellationToken)
    {
        var input = call.Input;
        if (TargetOf(call) is not { } agentId)
        {
            return TargetRefused(call);
        }
        if (input.Int("expectedVersion") is not { } expectedVersion)
        {
            return Missing("expectedVersion");
        }
        if (AutonomyOf(input) is { Refused: { } wrongAutonomy })
        {
            return wrongAutonomy;
        }
        if (await Directory.GetAsync(agentId, cancellationToken) is not { } current)
        {
            return Refused(NhAssistantErrorCodes.AgentNotFound, "The agent was not found.");
        }

        // Instructions left out keep what the agent has: its text, or the asset they come from.
        var newInstructions = input.RawText("instructions");
        var asset = newInstructions is null ? current.InstructionsAsset : null;
        var instructions = newInstructions ?? (asset is null ? current.Instructions : string.Empty);
        if (instructions is null)
        {
            return Refused(NhAssistantAdminErrorCodes.ValidationFailed, "The agent's instructions are not available now; send them in full.");
        }

        // Whose rights it uses, its policy and its MCP servers stay as they are.
        var request = new NhAssistantAgentDefinitionRequest(
            input.Text("displayName") ?? current.DisplayName,
            input.Text("description") ?? current.Description,
            instructions,
            input.Texts("toolSelectors") ?? current.ToolSelectors,
            AutonomyOf(input).Value ?? current.Autonomy,
            current.RequiredPolicy)
        {
            OfferedInChat = input.Bool("offeredInChat"),
            InstructionsAsset = asset
        };
        var updated = await Definitions.UpdateAsync(agentId, request, expectedVersion, call.Person, cancellationToken);
        return updated.Success ? Ok(new { agentId, updated = true, agent = updated.Data }) : From(updated, NhAssistantAdminErrorCodes.ValidationFailed);
    }

    public async Task<TaskResult<JsonElement>> SaveRunSettingsAsync(NhAssistantAgentBuilderCall call, CancellationToken cancellationToken)
    {
        var input = call.Input;
        if (TargetOf(call) is not { } agentId)
        {
            return TargetRefused(call);
        }

        var current = (await Configuration.GetAsync(agentId, cancellationToken))?.RunSettings;
        // Whose rights the runs use is never the builder's to change: the accountability, the technical
        // account, delegation and gateway and elevated reads stay, and new settings answer to the agent.
        // The file settings stay as well.
        var request = new NhAssistantAgentRunSettingsRequest(
            current?.Accountability ?? "agent",
            current?.ServiceAccountActorId,
            current?.DelegateFromInitiator ?? false,
            current?.AllowGatewayReads ?? false,
            current?.ElevatedReads,
            Pick(input, "approvalPolicy", current?.ApprovalPolicy),
            PickInt(input, "retryCount", current?.RetryCount),
            PickInt(input, "dailyToolCalls", current?.DailyToolCalls),
            PickLong(input, "dailyTokens", current?.DailyTokens),
            PickInt(input, "maxConcurrentRuns", current?.MaxConcurrentRuns),
            PickInt(input, "maxRunsPerSubjectPerDay", current?.MaxRunsPerSubjectPerDay),
            PickLong(input, "maxTokensPerRun", current?.MaxTokensPerRun),
            Pick(input, "triageProfile", current?.TriageProfile),
            Pick(input, "ruleHandler", current?.RuleHandler),
            input.Int("expectedVersion"),
            input.Text("reason"),
            current?.AcceptsFiles ?? false,
            current?.MaxFileBytes,
            current?.MaxFilePages,
            current?.MaxFilesPerTurn,
            current?.MaxFileTokensPerTurn);
        var saved = await Configuration.SaveRunSettingsAsync(agentId, request, call.Person, cancellationToken);
        return saved.Success ? Ok(new { agentId, runSettings = saved.Data }) : From(saved, NhAssistantAdminErrorCodes.ValidationFailed);
    }

    public async Task<TaskResult<JsonElement>> SaveSkillAsync(NhAssistantAgentBuilderCall call, CancellationToken cancellationToken)
    {
        var input = call.Input;
        if (TargetOf(call) is not { } agentId)
        {
            return TargetRefused(call);
        }
        if (Required(input, "skillId", "description") is { } missing)
        {
            return missing;
        }

        var skillId = input.Text("skillId")!;
        var current = (await Configuration.GetAsync(agentId, cancellationToken))?.DataSkills
            .FirstOrDefault(skill => string.Equals(skill.SkillId, skillId, StringComparison.Ordinal));
        var instructions = input.RawText("instructions");
        // The files a skill reads and an asset its instructions come from stay as they are.
        var request = new NhAssistantDataSkillRequest(
            input.Text("description"),
            Instructions: instructions,
            Tools: input.Texts("tools"),
            Profile: input.Text("profile"),
            RuleHandler: input.Text("ruleHandler"),
            MaxTokensPerRun: input.Long("maxTokensPerRun"),
            DailyToolCalls: input.Int("dailyToolCalls"),
            DailyTokens: input.Long("dailyTokens"),
            ApprovalPolicy: input.Text("approvalPolicy"),
            ExpectedVersion: input.Int("expectedVersion"),
            Reason: input.Text("reason"),
            Apply: input.Text("apply"),
            DataClassification: input.Text("dataClassification"),
            AcceptsFiles: current?.AcceptsFiles ?? false,
            MaxFileBytes: current?.MaxFileBytes,
            MaxFilePages: current?.MaxFilePages,
            MaxFilesPerTurn: current?.MaxFilesPerTurn,
            MaxFileTokensPerTurn: current?.MaxFileTokensPerTurn)
        {
            InstructionsAsset = instructions is null ? current?.InstructionsAsset : null
        };
        var saved = await Configuration.SaveSkillAsync(agentId, skillId, request, call.Person, cancellationToken);
        return saved.Success ? Ok(new { agentId, skill = saved.Data }) : From(saved, NhAssistantAdminErrorCodes.ValidationFailed);
    }

    public async Task<TaskResult<JsonElement>> SaveTriggerAsync(NhAssistantAgentBuilderCall call, CancellationToken cancellationToken)
    {
        var input = call.Input;
        if (TargetOf(call) is not { } agentId)
        {
            return TargetRefused(call);
        }
        if (Required(input, "triggerId", "source") is { } missing)
        {
            return missing;
        }

        var request = new NhAssistantDataTriggerRequest(
            input.Text("source"),
            input.Text("skill"),
            input.Text("event"),
            input.Text("cron"),
            input.Text("timeZone"),
            input.Text("subjectSet"),
            input.Text("message"),
            input.Text("conflict"),
            input.Int("debounceSeconds"),
            input.Int("expectedVersion"),
            input.Text("reason"),
            input.Texts("filters"))
        {
            Apply = input.Text("apply")
        };
        var saved = await Configuration.SaveTriggerAsync(agentId, input.Text("triggerId")!, request, call.Person, cancellationToken);
        return saved.Success ? Ok(new { agentId, trigger = saved.Data }) : From(saved, NhAssistantAdminErrorCodes.ValidationFailed);
    }

    public async Task<TaskResult<JsonElement>> SaveRuleScriptAsync(NhAssistantAgentBuilderCall call, CancellationToken cancellationToken)
    {
        var input = call.Input;
        if (TargetOf(call) is not { } agentId)
        {
            return TargetRefused(call);
        }
        if (Required(input, "script") is { } missing)
        {
            return missing;
        }

        var saved = await RuleScripts.SaveAsync(
            agentId,
            input.Text("skillId"),
            new NhAssistantRuleScriptSaveRequest(
                input.RawText("script"),
                input.Texts("writes") ?? [],
                input.Int("expectedRevision"),
                input.Text("reason"),
                input.Bool("live") ?? false,
                input.Bool("shadow") ?? false),
            call.Author,
            cancellationToken);
        return saved.Success ? Ok(new { agentId, version = saved.Data }) : From(saved, NhAssistantAdminErrorCodes.ValidationFailed);
    }

    public async Task<TaskResult<JsonElement>> SetLiveAsync(NhAssistantAgentBuilderCall call, CancellationToken cancellationToken)
    {
        var input = call.Input;
        if (TargetOf(call) is not { } agentId)
        {
            return TargetRefused(call);
        }
        if (input.Int("version") is not { } version)
        {
            return Missing("version");
        }

        var reason = input.Text("reason");
        switch (input.Text("part"))
        {
            case NhAssistantAgentBuilderTools.LiveParts.RuleScript:
            {
                var live = await RuleScripts.SetLiveAsync(
                    agentId,
                    input.Text("skillId"),
                    version,
                    new NhAssistantRuleScriptChangeRequest(input.Int("expectedRevision"), reason),
                    call.Author,
                    cancellationToken);
                return live.Success ? Ok(new { agentId, live = live.Data }) : From(live, NhAssistantRuleScriptCodes.NotCandidate);
            }
            case NhAssistantAgentBuilderTools.LiveParts.SkillCandidate:
            {
                if (input.Text("skillId") is not { } skillId)
                {
                    return Missing("skillId");
                }

                var promoted = await Configuration.PromoteSkillCandidateAsync(agentId, skillId, version, call.Person, reason, cancellationToken);
                return promoted.Success ? Ok(new { agentId, skill = promoted.Data }) : From(promoted, NhAssistantAdminErrorCodes.ValidationFailed);
            }
            case NhAssistantAgentBuilderTools.LiveParts.TriggerCandidate:
            {
                if (input.Text("triggerId") is not { } triggerId)
                {
                    return Missing("triggerId");
                }

                var promoted = await Configuration.PromoteTriggerCandidateAsync(agentId, triggerId, version, call.Person, reason, cancellationToken);
                return promoted.Success ? Ok(new { agentId, trigger = promoted.Data }) : From(promoted, NhAssistantAdminErrorCodes.ValidationFailed);
            }
            default:
                return Invalid("part");
        }
    }

    public async Task<TaskResult<JsonElement>> SetShadowAsync(NhAssistantAgentBuilderCall call, CancellationToken cancellationToken)
    {
        var input = call.Input;
        if (TargetOf(call) is not { } agentId)
        {
            return TargetRefused(call);
        }
        if (input.Bool("shadow") is not { } shadow)
        {
            return Missing("shadow");
        }

        var reason = input.Text("reason");
        switch (input.Text("part"))
        {
            case NhAssistantAgentBuilderTools.ShadowParts.RuleScript:
            {
                if (input.Int("version") is not { } version)
                {
                    return Missing("version");
                }

                var changed = await RuleScripts.SetShadowAsync(
                    agentId,
                    input.Text("skillId"),
                    version,
                    new NhAssistantRuleScriptShadowRequest(input.Int("expectedRevision"), shadow, reason),
                    call.Author,
                    cancellationToken);
                return changed.Success ? Ok(new { agentId, version = changed.Data }) : From(changed, NhAssistantRuleScriptCodes.NotCandidate);
            }
            case NhAssistantAgentBuilderTools.ShadowParts.Skill:
            {
                if (input.Text("skillId") is not { } skillId)
                {
                    return Missing("skillId");
                }

                var changed = await Control.SetSkillShadowAsync(agentId, skillId, shadow, call.Person, reason, cancellationToken);
                return changed.Success ? Ok(new { agentId, skill = changed.Data }) : From(changed, NhAssistantAdminErrorCodes.ValidationFailed);
            }
            case NhAssistantAgentBuilderTools.ShadowParts.Trigger:
            {
                if (input.Text("triggerId") is not { } triggerId)
                {
                    return Missing("triggerId");
                }

                var changed = await Configuration.SetTriggerShadowAsync(agentId, triggerId, shadow, call.Person, reason, cancellationToken);
                return changed.Success ? Ok(new { agentId, trigger = changed.Data }) : From(changed, NhAssistantAdminErrorCodes.ValidationFailed);
            }
            case NhAssistantAgentBuilderTools.ShadowParts.Agent:
            {
                var changed = await Control.SetShadowAsync(agentId, shadow, call.Person, reason, cancellationToken);
                return changed.Success ? Ok(new { agentId, state = changed.Data }) : From(changed, NhAssistantAdminErrorCodes.ValidationFailed);
            }
            default:
                return Invalid("part");
        }
    }

    // ----- Helpers -----

    /// <summary>
    /// What the code's ceiling of rule scripts allows, for the catalog; <see langword="null"/> while no agent
    /// may have rule scripts.
    /// </summary>
    private object? RuleScriptCeilingOf()
    {
        if (registration.RuleScriptsAsData is not { } ceiling)
        {
            return null;
        }

        return new
        {
            agents = ceiling.Agents
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => new
                {
                    agentId = item.Key,
                    writes = item.Value.Writes.Order(StringComparer.Ordinal).ToArray(),
                    item.Value.MaxWritesPerRun
                })
                .ToArray(),
            agentsCreatedInAdministration = ceiling.AgentsCreatedInAdministration is { } admin
                ? new { writes = admin.Writes.Order(StringComparer.Ordinal).ToArray(), admin.MaxWritesPerRun }
                : null,
            ceiling.RequireFourEyes
        };
    }

    /// <summary>
    /// The agent a call names: <see langword="null"/> when it names none or names the agent builder, which
    /// never works on itself.
    /// </summary>
    private static string? TargetOf(NhAssistantAgentBuilderCall call)
    {
        var agentId = call.Input.Text("agentId");
        return agentId is null || NhAssistantAgentBuilderIds.IsAgentBuilder(agentId) ? null : agentId;
    }

    private static TaskResult<JsonElement> TargetRefused(NhAssistantAgentBuilderCall call)
    {
        return call.Input.Text("agentId") is null
            ? Missing("agentId")
            : Refused(NhAssistantAdminErrorCodes.Forbidden, "The agent builder never changes or reads itself; name another agent.");
    }

    private static (NhAiAutonomyLevel? Value, TaskResult<JsonElement>? Refused) AutonomyOf(NhAssistantAgentBuilderInput input)
    {
        return input.Text("autonomy") switch
        {
            null => (null, null),
            "observe" => (NhAiAutonomyLevel.Observe, null),
            "explain" => (NhAiAutonomyLevel.Explain, null),
            "propose" => (NhAiAutonomyLevel.Propose, null),
            "simulate" => (NhAiAutonomyLevel.Simulate, null),
            "execute" => (NhAiAutonomyLevel.Execute, null),
            _ => (null, Invalid("autonomy"))
        };
    }

    private static TaskResult<JsonElement>? Required(NhAssistantAgentBuilderInput input, params string[] names)
    {
        var missing = names.Where(name => !input.Has(name)).ToArray();
        return missing.Length == 0
            ? null
            : Refused(NhAiToolFailureCodes.InputInvalid, "Give " + string.Join(", ", missing) + ".");
    }

    private static int? PickInt(NhAssistantAgentBuilderInput input, string name, int? current)
    {
        return input.Given(name) ? input.Int(name) : current;
    }

    private static long? PickLong(NhAssistantAgentBuilderInput input, string name, long? current)
    {
        return input.Given(name) ? input.Long(name) : current;
    }

    private static string? Pick(NhAssistantAgentBuilderInput input, string name, string? current)
    {
        return input.Given(name) ? input.Text(name) : current;
    }

    private static TaskResult<JsonElement> Missing(string name)
    {
        return Refused(NhAiToolFailureCodes.InputInvalid, $"Give {name}.");
    }

    private static TaskResult<JsonElement> Invalid(string name)
    {
        return Refused(NhAiToolFailureCodes.InputInvalid, $"'{name}' is not one of the values its schema names.");
    }

    private static TaskResult<JsonElement> Refused(string code, string message)
    {
        return TaskResult<JsonElement>.Failed(code, message);
    }

    /// <summary>
    /// A failure of a service as the model reads it: its code, its message and its field errors.
    /// </summary>
    private static TaskResult<JsonElement> From(TaskResult result, string fallback)
    {
        var code = NhAssistantResults.CodeOf(result, fallback);
        var message = result.GetResultItems()
            .Where(item => item.Name == code)
            .SelectMany(item => item.ErrorMessages)
            .Select(item => item.ToString())
            .FirstOrDefault() ?? "The administration refused the change.";
        var fields = NhAssistantResults.FieldErrorsOf(result);
        if (fields.Count > 0)
        {
            message += " Fields: " + string.Join("; ", fields.Select(field => field.Key + ": " + string.Join(", ", field.Value))) + ".";
        }

        return Refused(code, message);
    }

    private static TaskResult<JsonElement> Ok(object value)
    {
        return TaskResult<JsonElement>.Succeeded(JsonSerializer.SerializeToElement(value, value.GetType(), ResultOptions));
    }

    private static string EffectName(NhAiToolEffect effect)
    {
        return effect switch
        {
            NhAiToolEffect.ReadOnly => "read-only",
            NhAiToolEffect.IdempotentMutation => "idempotent-mutation",
            NhAiToolEffect.Mutation => "mutation",
            NhAiToolEffect.ExternalSideEffect => "external-side-effect",
            _ => "destructive"
        };
    }

    private sealed record CatalogTool(string Id, string Effect, string Description, string? SubjectType);

    private sealed record CatalogAlertKind(string Id, string NameKey, string Urgency, string? ModelTemplate);
}
