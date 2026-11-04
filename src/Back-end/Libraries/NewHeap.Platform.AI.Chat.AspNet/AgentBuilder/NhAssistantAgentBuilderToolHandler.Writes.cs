using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.AspNet.Mcp;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentBuilder;

/// <summary>
/// The builder's writes, each after the administrator's approval in the chat, through the services of the
/// administration's endpoints with the person as the actor. Input that is left out keeps what the agent
/// has; nothing is reset that the administrator did not mention.
/// </summary>
internal sealed partial class NhAssistantAgentBuilderToolHandler
{
    // ----- The definition -----

    public async Task<TaskResult<JsonNode>> CreateAgentAsync(NhAssistantAgentBuilderCall call, CancellationToken cancellationToken)
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
        if (NhAssistantAgentBuilderIds.IsAgentBuilder(input.Text("shadowOf")))
        {
            return Itself();
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
            input.Bool("perDivision") ?? false,
            input.Text("runPolicy"),
            input.Text("viewPolicy"),
            input.Bool("startsOff") ?? false,
            input.Text("profile"),
            input.Text("dataClassification"))
        {
            StartsInShadow = input.Bool("startsInShadow") ?? false,
            Compositions = input.Bool("compositions") ?? false,
            Scripts = input.Bool("scripts") ?? false,
            Metadata = input.Map("metadata"),
            ShadowOf = input.Text("shadowOf")
        };
        var created = await Configuration.CreateAgentAsync(request, call.Person, cancellationToken);
        // The agent's id comes first, so the administration can follow the new agent to its page.
        return Outcome(created, data => new { agentId = data.AgentId, created = true, configuration = data });
    }

    public async Task<TaskResult<JsonNode>> UpdateAgentAsync(NhAssistantAgentBuilderCall call, CancellationToken cancellationToken)
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

        var request = new NhAssistantAgentDefinitionRequest(
            input.Text("displayName") ?? current.DisplayName,
            input.Text("description") ?? current.Description,
            instructions,
            input.Texts("toolSelectors") ?? current.ToolSelectors,
            AutonomyOf(input).Value ?? current.Autonomy,
            Pick(input, "requiredPolicy", current.RequiredPolicy))
        {
            McpServerIds = input.Texts("mcpServerIds"),
            OfferedInChat = input.Bool("offeredInChat"),
            InstructionsAsset = asset,
            Profile = input.Text("profile"),
            DataClassification = input.Text("dataClassification"),
            Compositions = input.Bool("compositions"),
            Scripts = input.Bool("scripts")
        };
        request = await WithWorksOnAsync(request, input, agentId, cancellationToken);
        var updated = await Definitions.UpdateAsync(agentId, request, expectedVersion, call.Person, cancellationToken);
        return Outcome(updated, data => new { agentId, updated = true, agent = data });
    }

    /// <summary>
    /// What the agent works on goes together: when the call names one part, the parts it leaves out keep
    /// what the agent has.
    /// </summary>
    private async Task<NhAssistantAgentDefinitionRequest> WithWorksOnAsync(
        NhAssistantAgentDefinitionRequest request,
        NhAssistantAgentBuilderInput input,
        string agentId,
        CancellationToken cancellationToken)
    {
        string[] parts = ["worksOnSubjectTypes", "worksOnAllSubjects", "worksOnApplication", "perDivision", "runPolicy", "viewPolicy"];
        if (!parts.Any(input.Given))
        {
            return request;
        }

        var current = (await services.GetRequiredService<NhAssistantAgentCatalog>().FindAsync(agentId, includeDisabled: true, cancellationToken))?.WorksOn
            ?? NhAssistantAgentWorksOn.Undeclared;
        return request with
        {
            WorksOnSubjectTypes = input.Texts("worksOnSubjectTypes") ?? current.SubjectTypes,
            WorksOnAllSubjects = input.Bool("worksOnAllSubjects") ?? current.AllSubjects,
            WorksOnApplication = input.Bool("worksOnApplication") ?? current.Application,
            PerDivision = input.Bool("perDivision") ?? current.PerDivision,
            RunPolicy = Pick(input, "runPolicy", current.RunPolicy),
            ViewPolicy = Pick(input, "viewPolicy", current.ViewPolicy)
        };
    }

    // ----- How it runs -----

    public async Task<TaskResult<JsonNode>> SaveRunSettingsAsync(NhAssistantAgentBuilderCall call, CancellationToken cancellationToken)
    {
        var input = call.Input;
        if (TargetOf(call) is not { } agentId)
        {
            return TargetRefused(call);
        }

        var current = (await Configuration.GetAsync(agentId, cancellationToken))?.RunSettings;
        var accountability = Pick(input, "accountability", current?.Accountability) ?? NhAssistantAccountabilities.Agent;
        // A technical account belongs to service-account only: another accountability without one named
        // leaves it out.
        var serviceAccount = input.Given("serviceAccountActorId")
            ? input.Text("serviceAccountActorId")
            : string.Equals(accountability, NhAssistantAccountabilities.ServiceAccount, StringComparison.Ordinal) ? current?.ServiceAccountActorId : null;
        var request = new NhAssistantAgentRunSettingsRequest(
            accountability,
            serviceAccount,
            PickBool(input, "delegateFromInitiator", current?.DelegateFromInitiator ?? false),
            PickBool(input, "allowGatewayReads", current?.AllowGatewayReads ?? false),
            Pick(input, "elevatedReads", current?.ElevatedReads),
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
            PickBool(input, "acceptsFiles", current?.AcceptsFiles ?? false),
            PickInt(input, "maxFileBytes", current?.MaxFileBytes),
            PickInt(input, "maxFilePages", current?.MaxFilePages),
            PickInt(input, "maxFilesPerTurn", current?.MaxFilesPerTurn),
            PickInt(input, "maxFileTokensPerTurn", current?.MaxFileTokensPerTurn))
        {
            // The kinds the agent's rules and model may raise (FB93): left out keeps the stored list.
            AlertKinds = input.Texts("alertKinds")
        };
        var saved = await Configuration.SaveRunSettingsAsync(agentId, request, call.Person, cancellationToken);
        return Outcome(saved, data => new { agentId, runSettings = data });
    }

    public async Task<TaskResult<JsonNode>> SaveSkillAsync(NhAssistantAgentBuilderCall call, CancellationToken cancellationToken)
    {
        var input = call.Input;
        if (TargetOf(call) is not { } agentId)
        {
            return TargetRefused(call);
        }
        if (Required(input, "skillId") is { } missing)
        {
            return missing;
        }

        var skillId = input.Text("skillId")!;
        var current = (await Configuration.GetAsync(agentId, cancellationToken))?.DataSkills
            .FirstOrDefault(skill => string.Equals(skill.SkillId, skillId, StringComparison.Ordinal));
        if (Pick(input, "description", current?.Description) is not { } description)
        {
            return Missing("description");
        }

        // The service stores the whole skill: what the call leaves out keeps the live version's value, as
        // the card says, and null clears a value that may be empty. The file limits stay as they are.
        var instructionsGiven = input.Given("instructions");
        var request = new NhAssistantDataSkillRequest(
            description,
            Instructions: instructionsGiven ? input.RawText("instructions") : current?.InstructionsAsset is null ? current?.Instructions : null,
            Tools: input.Texts("tools") ?? current?.Tools,
            Profile: Pick(input, "profile", current?.Profile),
            RuleHandler: Pick(input, "ruleHandler", current?.RuleHandler),
            MaxTokensPerRun: PickLong(input, "maxTokensPerRun", current?.MaxTokensPerRun),
            DailyToolCalls: PickInt(input, "dailyToolCalls", current?.DailyToolCalls),
            DailyTokens: PickLong(input, "dailyTokens", current?.DailyTokens),
            ApprovalPolicy: Pick(input, "approvalPolicy", current?.ApprovalPolicy),
            ExpectedVersion: input.Int("expectedVersion"),
            Reason: input.Text("reason"),
            Apply: input.Text("apply"),
            DataClassification: Pick(input, "dataClassification", current?.DataClassification),
            AcceptsFiles: input.Bool("acceptsFiles") ?? current?.AcceptsFiles ?? false,
            MaxFileBytes: current?.MaxFileBytes,
            MaxFilePages: current?.MaxFilePages,
            MaxFilesPerTurn: current?.MaxFilesPerTurn,
            MaxFileTokensPerTurn: current?.MaxFileTokensPerTurn)
        {
            InstructionsAsset = instructionsGiven ? null : current?.InstructionsAsset
        };
        var saved = await Configuration.SaveSkillAsync(agentId, skillId, request, call.Person, cancellationToken);
        return Outcome(saved, data => new { agentId, skill = data });
    }

    public async Task<TaskResult<JsonNode>> SaveTriggerAsync(NhAssistantAgentBuilderCall call, CancellationToken cancellationToken)
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

        // The service stores the whole trigger: while the source stays the same, what the call leaves out
        // keeps the live version's value, as the card says; another source starts from what the call gives.
        var triggerId = input.Text("triggerId")!;
        var source = input.Text("source")!;
        var current = (await Configuration.GetAsync(agentId, cancellationToken))?.DataTriggers
            .FirstOrDefault(trigger => string.Equals(trigger.TriggerId, triggerId, StringComparison.Ordinal));
        var same = current is not null && string.Equals(current.Source, source, StringComparison.Ordinal);
        string? Keep(string name, string? value)
        {
            return input.Given(name) ? input.Text(name) : same ? value : null;
        }

        var events = string.Equals(source, "event", StringComparison.Ordinal);
        var request = new NhAssistantDataTriggerRequest(
            source,
            Keep("skill", current?.SkillId),
            Keep("event", current?.Event),
            Keep("cron", current?.Cron),
            Keep("timeZone", current?.TimeZone),
            Keep("subjectSet", current?.SubjectSet),
            Keep("message", current?.Message),
            Keep("conflict", current?.Conflict),
            input.Given("debounceSeconds") ? input.Int("debounceSeconds") : same && events ? current?.DebounceSeconds : null,
            input.Int("expectedVersion"),
            input.Text("reason"),
            input.Texts("filters") ?? (same && events ? current?.Filters : null))
        {
            Apply = input.Text("apply")
        };
        var saved = await Configuration.SaveTriggerAsync(agentId, triggerId, request, call.Person, cancellationToken);
        return Outcome(saved, data => new { agentId, trigger = data });
    }

    public async Task<TaskResult<JsonNode>> SaveRuleScriptAsync(NhAssistantAgentBuilderCall call, CancellationToken cancellationToken)
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
        return Outcome(saved, data => new { agentId, version = data });
    }

    public async Task<TaskResult<JsonNode>> SaveConsultationAsync(NhAssistantAgentBuilderCall call, CancellationToken cancellationToken)
    {
        var input = call.Input;
        if (TargetOf(call) is not { } agentId)
        {
            return TargetRefused(call);
        }
        if (Required(input, "subjectType") is { } missing)
        {
            return missing;
        }

        var subjectType = input.Text("subjectType")!;
        var current = (await Configuration.GetAsync(agentId, cancellationToken))?.DataConsultations
            .FirstOrDefault(consultation => string.Equals(consultation.SubjectType, subjectType, StringComparison.Ordinal));
        if (Pick(input, "description", current?.Description) is not { } description)
        {
            return Missing("description");
        }

        // The service stores the whole consultation: what the call leaves out keeps the current value, as
        // the card says. A consultation that takes requests only has no turn of its own, so the settings of
        // that turn are not carried over to it.
        var requestsOnly = input.Bool("requestsOnly") ?? current?.RequestsOnly ?? false;
        var turn = requestsOnly ? null : current;
        var request = new NhAssistantDataConsultationRequest(
            description,
            input.Given("instructions") ? input.RawText("instructions") : turn?.Instructions,
            Pick(input, "minimumRole", turn?.MinimumRole),
            Pick(input, "askerPolicy", current?.AskerPolicy),
            input.Texts("routes") ?? turn?.Routes,
            Pick(input, "proposalRole", turn?.ProposalRole),
            Pick(input, "proposalPolicy", turn?.ProposalPolicy),
            PickInt(input, "maxToolCalls", turn?.MaxToolCalls),
            PickInt(input, "timeoutSeconds", turn?.TimeoutSeconds),
            PickInt(input, "maxAnswerChars", turn?.MaxAnswerChars),
            PickInt(input, "maxHistoryMessages", turn?.MaxHistoryMessages),
            input.Bool("allowRequests") ?? current?.AllowRequests ?? false,
            input.Bool("exposeToMcp") ?? current?.ExposeToMcp ?? false,
            Pick(input, "dataClassification", current?.DataClassification),
            input.Int("expectedVersion"),
            input.Text("reason"))
        {
            ToolSelectors = input.Texts("toolSelectors") ?? turn?.ToolSelectors,
            RequestsOnly = requestsOnly,
            StartsOff = input.Bool("startsOff") ?? false
        };
        var saved = await Configuration.SaveConsultationAsync(agentId, subjectType, request, call.Person, cancellationToken);
        return Outcome(saved, data => new { agentId, consultation = data });
    }

    public async Task<TaskResult<JsonNode>> SaveHandOffAsync(NhAssistantAgentBuilderCall call, CancellationToken cancellationToken)
    {
        var input = call.Input;
        if (TargetOf(call) is not { } agentId)
        {
            return TargetRefused(call);
        }
        if (Required(input, "subjectType", "sources") is { } missing)
        {
            return missing;
        }

        var sources = input.Texts("sources")!;
        if (sources.Any(NhAssistantAgentBuilderIds.IsAgentBuilder))
        {
            return Itself();
        }

        var saved = await HandOffs.SaveAsync(
            agentId,
            input.Text("subjectType")!,
            new NhAssistantDataHandOffRequest(
                sources,
                input.Text("description"),
                input.Bool("notifySource") ?? false,
                input.Int("maxPerSubjectPerDay"),
                input.Int("expectedVersion"),
                input.Text("reason")),
            call.Person,
            cancellationToken);
        return Outcome(saved, data => new { agentId, handOff = data });
    }

    public async Task<TaskResult<JsonNode>> SaveMetadataAsync(NhAssistantAgentBuilderCall call, CancellationToken cancellationToken)
    {
        var input = call.Input;
        if (TargetOf(call) is not { } agentId)
        {
            return TargetRefused(call);
        }
        if (Required(input, "name") is { } missing)
        {
            return missing;
        }
        if (input.Element("value") is not { } value)
        {
            return Missing("value");
        }

        var saved = await Configuration.SaveMetadataAsync(
            agentId,
            input.Text("name")!,
            new NhAssistantAgentMetadataRequest(value, input.Int("expectedVersion")),
            call.Person,
            cancellationToken);
        return Outcome(saved, data => new { agentId, metadata = data });
    }

    // ----- Live, shadow and state -----

    public async Task<TaskResult<JsonNode>> SetLiveAsync(NhAssistantAgentBuilderCall call, CancellationToken cancellationToken)
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
                return Outcome(live, data => new { agentId, live = data }, NhAssistantRuleScriptCodes.NotCandidate);
            }
            case NhAssistantAgentBuilderTools.LiveParts.SkillCandidate:
            {
                if (input.Text("skillId") is not { } skillId)
                {
                    return Missing("skillId");
                }

                var promoted = await Configuration.PromoteSkillCandidateAsync(agentId, skillId, version, call.Person, reason, cancellationToken);
                return Outcome(promoted, data => new { agentId, skill = data });
            }
            case NhAssistantAgentBuilderTools.LiveParts.TriggerCandidate:
            {
                if (input.Text("triggerId") is not { } triggerId)
                {
                    return Missing("triggerId");
                }

                var promoted = await Configuration.PromoteTriggerCandidateAsync(agentId, triggerId, version, call.Person, reason, cancellationToken);
                return Outcome(promoted, data => new { agentId, trigger = data });
            }
            default:
                return Invalid("part");
        }
    }

    public async Task<TaskResult<JsonNode>> SetShadowAsync(NhAssistantAgentBuilderCall call, CancellationToken cancellationToken)
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
                return Outcome(changed, data => new { agentId, version = data }, NhAssistantRuleScriptCodes.NotCandidate);
            }
            case NhAssistantAgentBuilderTools.ShadowParts.Skill:
            {
                if (input.Text("skillId") is not { } skillId)
                {
                    return Missing("skillId");
                }

                var changed = await Control.SetSkillShadowAsync(agentId, skillId, shadow, call.Person, reason, cancellationToken);
                return Outcome(changed, data => new { agentId, skill = data });
            }
            case NhAssistantAgentBuilderTools.ShadowParts.Trigger:
            {
                if (input.Text("triggerId") is not { } triggerId)
                {
                    return Missing("triggerId");
                }

                var changed = await Configuration.SetTriggerShadowAsync(agentId, triggerId, shadow, call.Person, reason, cancellationToken);
                return Outcome(changed, data => new { agentId, trigger = data });
            }
            case NhAssistantAgentBuilderTools.ShadowParts.Agent:
            {
                var changed = await Control.SetShadowAsync(agentId, shadow, call.Person, reason, cancellationToken);
                return Outcome(changed, data => new { agentId, state = data });
            }
            default:
                return Invalid("part");
        }
    }

    public async Task<TaskResult<JsonNode>> SetStateAsync(NhAssistantAgentBuilderCall call, CancellationToken cancellationToken)
    {
        var input = call.Input;
        if (TargetOf(call) is not { } agentId)
        {
            return TargetRefused(call);
        }
        if (input.Text("state") is not { } state)
        {
            return Missing("state");
        }

        var reason = input.Text("reason");
        switch (input.Text("part"))
        {
            case NhAssistantAgentBuilderTools.StateParts.Agent:
            {
                var changed = state switch
                {
                    NhAssistantAgentRunStates.Paused => await Control.PauseAsync(agentId, call.Person, reason, cancellationToken),
                    NhAssistantAgentRunStates.Stopped => await Control.StopAsync(agentId, call.Person, reason, cancellationToken),
                    NhAssistantAgentRunStates.Off => await Control.SwitchOffAsync(agentId, call.Person, reason, cancellationToken),
                    NhAssistantAgentRunStates.Active => await Control.ResumeAsync(agentId, call.Person, reason, cancellationToken),
                    _ => null
                };
                return changed is null ? Invalid("state") : Outcome(changed, data => new { agentId, state = data });
            }
            case NhAssistantAgentBuilderTools.StateParts.Skill:
            {
                if (input.Text("skillId") is not { } skillId)
                {
                    return Missing("skillId");
                }

                var changed = state switch
                {
                    NhAssistantAgentRunStates.Paused => await Control.PauseSkillAsync(agentId, skillId, call.Person, reason, cancellationToken),
                    NhAssistantAgentRunStates.Stopped => await Control.StopSkillAsync(agentId, skillId, call.Person, reason, cancellationToken),
                    NhAssistantAgentRunStates.Off => await Control.SwitchOffSkillAsync(agentId, skillId, call.Person, reason, cancellationToken),
                    NhAssistantAgentRunStates.Active => await Control.ResumeSkillAsync(agentId, skillId, call.Person, reason, cancellationToken),
                    _ => null
                };
                return changed is null ? Invalid("state") : Outcome(changed, data => new { agentId, skill = data });
            }
            case NhAssistantAgentBuilderTools.StateParts.Consultation:
            {
                if (input.Text("subjectType") is not { } subjectType)
                {
                    return Missing("subjectType");
                }
                if (state is not (NhAssistantConsultationStates.On or NhAssistantConsultationStates.Off))
                {
                    return Invalid("state");
                }

                var changed = await Configuration.SetConsultationStateAsync(
                    agentId,
                    subjectType,
                    state == NhAssistantConsultationStates.On,
                    call.Person,
                    reason,
                    cancellationToken);
                return Outcome(changed, data => new { agentId, consultation = data });
            }
            default:
                return Invalid("part");
        }
    }

    // ----- Whose rights the agent uses -----

    public async Task<TaskResult<JsonNode>> SetTechnicalAccountAsync(NhAssistantAgentBuilderCall call, CancellationToken cancellationToken)
    {
        if (TargetOf(call) is not { } agentId)
        {
            return TargetRefused(call);
        }
        if (!call.Input.Given("actorId"))
        {
            return Missing("actorId");
        }

        var changed = await Control.SetServiceAccountAsync(agentId, call.Input.Text("actorId"), call.Person, call.Input.Text("reason"), cancellationToken);
        return Outcome(changed, data => new { agentId, state = data });
    }

    public async Task<TaskResult<JsonNode>> SetConsultationRoutesAsync(NhAssistantAgentBuilderCall call, CancellationToken cancellationToken)
    {
        var input = call.Input;
        if (TargetOf(call) is not { } agentId)
        {
            return TargetRefused(call);
        }

        var agent = await services.GetRequiredService<NhAssistantAgentCatalog>().FindAsync(agentId, includeDisabled: true, cancellationToken);
        var current = agent?.ConsultationRoutes ?? NhAssistantConsultationRouteOverrides.None;
        if (!NhAssistantRouteSwitches.TryApply(input.Text("askerDecides"), current.AskerDecides, out var askerDecides))
        {
            return Invalid("askerDecides");
        }
        if (!NhAssistantRouteSwitches.TryApply(input.Text("subjectAgentRights"), current.SubjectAgentRights, out var agentRights))
        {
            return Invalid("subjectAgentRights");
        }
        if (!NhAssistantRouteSwitches.TryApply(input.Text("subjectAskerRights"), current.SubjectAskerRights, out var askerRights))
        {
            return Invalid("subjectAskerRights");
        }

        var changed = await Control.SetConsultationRoutesAsync(
            agentId,
            new NhAssistantConsultationRouteOverrides(askerDecides, agentRights, askerRights),
            call.Person,
            input.Text("reason"),
            cancellationToken);
        return Outcome(changed, data => new { agentId, state = data });
    }

    public async Task<TaskResult<JsonNode>> SetMcpServerAsync(NhAssistantAgentBuilderCall call, CancellationToken cancellationToken)
    {
        var input = call.Input;
        if (TargetOf(call) is not { } agentId)
        {
            return TargetRefused(call);
        }
        if (Required(input, "serverId", "assigned") is { } missing)
        {
            return missing;
        }

        var serverId = input.Text("serverId")!;
        var assigned = input.Bool("assigned")!.Value;
        var changed = assigned
            ? await McpServers.AssignAsync(serverId, agentId, call.Person, cancellationToken)
            : await McpServers.UnassignAsync(serverId, agentId, call.Person, cancellationToken);
        // The server is returned without its address, connection or credentials.
        return Outcome(changed, data => new { agentId, serverId, assigned, data.AssignedAgentIds });
    }

    public async Task<TaskResult<JsonNode>> SetMcpHeadlessUseAsync(NhAssistantAgentBuilderCall call, CancellationToken cancellationToken)
    {
        var input = call.Input;
        if (Required(input, "serverId", "headlessUse") is { } missing)
        {
            return missing;
        }

        // Only headless use changes, through the administration's own check of that field: the server's other
        // settings, its connection and its secret are never read or written here, so a change an administrator
        // makes in the screens meanwhile stays.
        var serverId = input.Text("serverId")!;
        var changed = await services.GetRequiredService<NhAssistantMcpAdministration>()
            .SetHeadlessUseAsync(serverId, input.Text("headlessUse")!, call.Person, cancellationToken);        return Outcome(changed, data => new { serverId, data.HeadlessUse });
    }

    // ----- Switches, groups and shadow variants -----

    public async Task<TaskResult<JsonNode>> SetSwitchAsync(NhAssistantAgentBuilderCall call, CancellationToken cancellationToken)
    {
        var input = call.Input;
        if (TargetOf(call) is not { } agentId)
        {
            return TargetRefused(call);
        }
        if (Required(input, "mode", "expectedVersion") is { } missing)
        {
            return missing;
        }

        var mode = input.Text("mode");
        var expectedVersion = input.Int("expectedVersion");
        var reason = input.Text("reason");
        switch (input.Text("part"))
        {
            case NhAssistantAgentBuilderTools.SwitchParts.ToolWithoutApproval:
            {
                if (input.Text("toolId") is not { } toolId)
                {
                    return Missing("toolId");
                }

                var changed = await WithoutApproval.SetAsync(agentId, toolId, new NhAssistantWithoutApprovalRequest(mode, expectedVersion, reason), call.Person, cancellationToken);
                return Outcome(changed, data => new { agentId, toolWithoutApproval = data });
            }
            case NhAssistantAgentBuilderTools.SwitchParts.AlertModel:
            {
                if (input.Text("kindId") is not { } kindId)
                {
                    return Missing("kindId");
                }

                var changed = await AlertModel.SetAsync(agentId, kindId, new NhAssistantAlertModelRequest(mode, expectedVersion, reason), call.Person, cancellationToken);
                return Outcome(changed, data => new { agentId, alertModel = data });
            }
            default:
                return Invalid("part");
        }
    }

    public async Task<TaskResult<JsonNode>> SetGroupsAsync(NhAssistantAgentBuilderCall call, CancellationToken cancellationToken)
    {
        if (TargetOf(call) is not { } agentId)
        {
            return TargetRefused(call);
        }
        if (call.Input.Texts("groups") is not { } groups)
        {
            return Missing("groups");
        }

        var changed = await Groups.SetMembershipAsync(agentId, groups, call.Person, cancellationToken);
        return Outcome(changed, data => new { agentId, groups = data });
    }

    public async Task<TaskResult<JsonNode>> PromoteShadowVariantAsync(NhAssistantAgentBuilderCall call, CancellationToken cancellationToken)
    {
        var input = call.Input;
        if (TargetOf(call) is not { } variantId)
        {
            return TargetRefused(call);
        }
        if (input.Int("expectedVersion") is not { } expectedVersion)
        {
            return Missing("expectedVersion");
        }

        var uncarriedParts = input.Text("uncarriedParts") switch
        {
            null or "unlink" => NhAssistantUncarriedParts.Unlink,
            "relink" => NhAssistantUncarriedParts.Relink,
            _ => (NhAssistantUncarriedParts?)null
        };
        if (uncarriedParts is null)
        {
            return Invalid("uncarriedParts");
        }

        var promoted = await Definitions.PromoteShadowVariantAsync(
            variantId,
            new NhAssistantShadowVariantPromotionRequest(
                expectedVersion,
                input.Bool("moveSkills") ?? false,
                input.Int("expectedVariantVersion"),
                input.Text("reason"))
            {
                AsPackageVersion = input.Bool("asPackageVersion") ?? false,
                UncarriedParts = uncarriedParts.Value
            },
            call.Person,
            cancellationToken);
        return Outcome(promoted, data => new { agentId = data.Agent.AgentId, promoted = data });
    }

    // ----- Removing and discarding -----

    public async Task<TaskResult<JsonNode>> RemoveAsync(NhAssistantAgentBuilderCall call, CancellationToken cancellationToken)
    {
        var input = call.Input;
        if (TargetOf(call) is not { } agentId)
        {
            return TargetRefused(call);
        }

        var part = input.Text("part");
        var reason = input.Text("reason");
        var expectedVersion = input.Int("expectedVersion");
        switch (part)
        {
            case NhAssistantAgentBuilderTools.RemoveParts.Agent:
                return Removed(await Definitions.DeleteAsync(agentId, call.Person, cancellationToken), agentId, part);
            case NhAssistantAgentBuilderTools.RemoveParts.Override:
                return Outcome(await Definitions.ResetAsync(agentId, call.Person, cancellationToken), data => new { agentId, removed = part, agent = data });
            case NhAssistantAgentBuilderTools.RemoveParts.RunSettings:
                return Removed(await Configuration.RemoveRunSettingsAsync(agentId, expectedVersion, call.Person, reason, cancellationToken), agentId, part);
            case NhAssistantAgentBuilderTools.RemoveParts.Skill:
                return input.Text("skillId") is { } skillId
                    ? Removed(await Configuration.RemoveSkillAsync(agentId, skillId, expectedVersion, call.Person, reason, cancellationToken), agentId, part)
                    : Missing("skillId");
            case NhAssistantAgentBuilderTools.RemoveParts.Trigger:
                return input.Text("triggerId") is { } triggerId
                    ? Removed(await Configuration.RemoveTriggerAsync(agentId, triggerId, expectedVersion, call.Person, reason, cancellationToken), agentId, part)
                    : Missing("triggerId");
            case NhAssistantAgentBuilderTools.RemoveParts.Consultation:
                return input.Text("subjectType") is { } consultationType
                    ? Removed(await Configuration.RemoveConsultationAsync(agentId, consultationType, expectedVersion, call.Person, reason, cancellationToken), agentId, part)
                    : Missing("subjectType");
            case NhAssistantAgentBuilderTools.RemoveParts.HandOff:
                return input.Text("subjectType") is { } handOffType
                    ? Removed(await HandOffs.RemoveAsync(agentId, handOffType, expectedVersion, call.Person, reason, cancellationToken), agentId, part)
                    : Missing("subjectType");
            case NhAssistantAgentBuilderTools.RemoveParts.Metadata:
                return input.Text("name") is { } name
                    ? Outcome(await Configuration.RemoveMetadataAsync(agentId, name, expectedVersion, call.Person, cancellationToken), data => new { agentId, removed = part, metadata = data })
                    : Missing("name");
            case NhAssistantAgentBuilderTools.RemoveParts.SkillCandidate:
                return input.Text("skillId") is not { } candidateSkill
                    ? Missing("skillId")
                    : input.Int("version") is not { } skillVersion
                        ? Missing("version")
                        : Removed(await Configuration.DiscardSkillCandidateAsync(agentId, candidateSkill, skillVersion, call.Person, reason, cancellationToken), agentId, part);
            case NhAssistantAgentBuilderTools.RemoveParts.TriggerCandidate:
                return input.Text("triggerId") is not { } candidateTrigger
                    ? Missing("triggerId")
                    : input.Int("version") is not { } triggerVersion
                        ? Missing("version")
                        : Removed(await Configuration.DiscardTriggerCandidateAsync(agentId, candidateTrigger, triggerVersion, call.Person, reason, cancellationToken), agentId, part);
            case NhAssistantAgentBuilderTools.RemoveParts.RuleScriptVersion:
            {
                if (input.Int("version") is not { } version)
                {
                    return Missing("version");
                }

                var discarded = await RuleScripts.DiscardAsync(
                    agentId,
                    input.Text("skillId"),
                    version,
                    new NhAssistantRuleScriptChangeRequest(input.Int("expectedRevision"), reason),
                    call.Author,
                    cancellationToken);
                return Outcome(discarded, data => new { agentId, removed = part, version = data }, NhAssistantRuleScriptCodes.NotCandidate);
            }
            default:
                return Invalid("part");
        }
    }

    private static TaskResult<JsonNode> Removed(TaskResult result, string agentId, string part)
    {
        return result.Success ? Ok(new { agentId, removed = part }) : From(result, NhAssistantAdminErrorCodes.ValidationFailed);
    }
}
