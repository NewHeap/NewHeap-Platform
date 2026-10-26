using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// The check of the whole package against the host as it would be after the import (PK17), and the
/// one transaction that writes it with its links (PK6, PK11).
/// </summary>
internal sealed partial class NhAssistantAgentPackageService
{
    /// <summary>
    /// Builds the run configuration the host would have with the package applied, the package's
    /// agents and parts on top of what the host has, and refuses every part the load of that
    /// configuration would leave out of effect, with the codes its save gives. References between
    /// agents of the package resolve, cycles included; references outside it must exist on the host.
    /// </summary>
    private async Task CheckAgainstHostAsync(NhAssistantPackagePlan plan, NhAssistantStoredRunConfiguration stored, CancellationToken cancellationToken)
    {
        var adminAgents = new Dictionary<string, NhAssistantStoredAdminAgent>(stored.AdminAgents, StringComparer.Ordinal);
        var agentRuns = stored.AgentRuns.ToList();
        var skills = stored.Skills.ToList();
        var triggers = stored.Triggers.ToList();
        var consultations = stored.Consultations.ToList();
        var handOffs = stored.HandOffs.ToList();
        if (plan.Promotion is { } promotion)
        {
            // The promotion deletes the variant with everything that belongs to it (FB11).
            adminAgents.Remove(promotion.VariantId);
            agentRuns.RemoveAll(item => item.AgentId == promotion.VariantId);
            skills.RemoveAll(item => item.AgentId == promotion.VariantId);
            triggers.RemoveAll(item => item.AgentId == promotion.VariantId);
            consultations.RemoveAll(item => item.AgentId == promotion.VariantId);
            handOffs.RemoveAll(item => item.AgentId == promotion.VariantId);
        }

        foreach (var agent in plan.Agents.Where(item => !item.InCode && item.Row is not null))
        {
            var row = agent.Row!;
            adminAgents[agent.Id] = new NhAssistantStoredAdminAgent(
                row.ToolSelectorsJson,
                NhAssistantAgentWorksOn.FromRow(row),
                row.DataClassification,
                row.RequiredPolicy,
                NhAssistantInstructionAssets.ReferenceOf(row))
            {
                ShadowOfAgentId = row.ShadowOfAgentId
            };
        }

        foreach (var part in plan.Parts.Where(item => item.Linked && item.Row is not null))
        {
            switch (part.Row)
            {
                case AssistantAgentRunConfiguration settings:
                    agentRuns.RemoveAll(item => item.AgentId == part.AgentId);
                    agentRuns.Add(settings);
                    break;
                case AssistantAgentSkillDefinition skill:
                    var latestSkill = skills.Where(item => item.AgentId == part.AgentId && item.SkillId == part.Key).Select(item => item.Version).DefaultIfEmpty(0).Max();
                    skills.RemoveAll(item => item.AgentId == part.AgentId
                                             && item.SkillId == part.Key
                                             && (item.Status == NhAssistantSkillDefinitionStatuses.Candidate || !part.AsCandidate));
                    skill.Version = latestSkill + 1;
                    skill.Status = part.AsCandidate ? NhAssistantSkillDefinitionStatuses.Candidate : NhAssistantSkillDefinitionStatuses.Live;
                    skills.Add(skill);
                    break;
                case AssistantAgentTriggerDefinition trigger:
                    var live = triggers.FirstOrDefault(item => item.Id == part.Key && item.Status == NhAssistantSkillDefinitionStatuses.Live);
                    var latestTrigger = triggers.Where(item => item.Id == part.Key).Select(item => item.Version).DefaultIfEmpty(0).Max();
                    triggers.RemoveAll(item => item.Id == part.Key
                                               && (item.Status == NhAssistantSkillDefinitionStatuses.Candidate || !part.AsCandidate));
                    trigger.Version = latestTrigger + 1;
                    trigger.Status = part.AsCandidate ? NhAssistantSkillDefinitionStatuses.Candidate : NhAssistantSkillDefinitionStatuses.Live;
                    trigger.Shadow = live?.Shadow ?? true;
                    triggers.Add(trigger);
                    break;
                case AssistantAgentConsultationDefinition consultation:
                    consultations.RemoveAll(item => item.AgentId == part.AgentId && item.SubjectType == part.Key);
                    consultation.Version = (part.ExpectedVersion ?? 0) + 1;
                    consultations.Add(consultation);
                    break;
                case AssistantAgentHandOffDefinition handOff:
                    handOffs.RemoveAll(item => item.AgentId == part.AgentId && item.SubjectType == part.Key);
                    handOff.Version = (part.ExpectedVersion ?? 0) + 1;
                    handOffs.Add(handOff);
                    break;
            }
        }

        var hypothetical = stored with
        {
            AgentRuns = agentRuns,
            AdminAgents = adminAgents,
            Skills = skills,
            Triggers = triggers,
            Consultations = consultations,
            HandOffs = handOffs
        };

        // The assets the package names are asked for as every load asks for them (IA6).
        var assets = skills
            .Select(NhAssistantInstructionAssets.ReferenceOf)
            .Concat(adminAgents.Values.Select(item => item.InstructionsAsset))
            .OfType<NhAssistantInstructionAssetReference>()
            .Select(reference => (reference.AssetId, reference.Version))
            .Distinct()
            .ToArray();
        await instructionAssets.RefreshAsync(assets, cancellationToken);
        var snapshot = configurations.Build(hypothetical);
        foreach (var part in plan.Parts.Where(item => item.Linked && !item.Refused))
        {
            RefuseProblemsOf(part, snapshot.Problems);
        }

        RefuseBeyondLimits(plan, triggers, consultations, handOffs);
    }

    /// <summary>
    /// Refuses a part with what its save answers to the problems the load gives it.
    /// </summary>
    private void RefuseProblemsOf(NhAssistantPlannedPart part, IReadOnlyList<NhAssistantRunConfigurationProblem> problems)
    {
        var own = problems.Where(problem => problem.AgentId == part.AgentId).ToArray();
        switch (part.Kind)
        {
            case NhAssistantPackagePartKinds.Agent:
                foreach (var problem in own.Where(item => item.Kind == NhAssistantRunConfigurationProblems.AgentKind && IsDefinitionProblem(item.Code)))
                {
                    part.Refuse(DefinitionFailure(problem.Code));
                }

                break;
            case NhAssistantPackagePartKinds.RunSettings:
                foreach (var problem in own.Where(item => item.Kind == NhAssistantRunConfigurationProblems.AgentKind && !IsDefinitionProblem(item.Code)))
                {
                    part.Refuse(problem.Code switch
                    {
                        NhAssistantRunConfigurationProblems.ConflictsWithCode => TaskResult.Failed(NhAssistantErrorCodes.RunSettingsInCode, "The agent is registered in code."),
                        _ => NhAssistantAgentConfiguration.RunSettingsFieldError(problem.Code)
                    });
                }

                break;
            case NhAssistantPackagePartKinds.Skill:
                var skillKind = part.AsCandidate ? NhAssistantRunConfigurationProblems.CandidateKind : NhAssistantRunConfigurationProblems.SkillKind;
                foreach (var problem in own.Where(item => item.Kind == skillKind && item.Id == part.Key))
                {
                    part.Refuse(SkillFailure(problem.Code));
                }

                break;
            case NhAssistantPackagePartKinds.Trigger:
                var triggerKind = part.AsCandidate ? NhAssistantRunConfigurationProblems.TriggerCandidateKind : NhAssistantRunConfigurationProblems.TriggerKind;
                var schedule = (part.Row as AssistantAgentTriggerDefinition)?.Source == NhAssistantDataTriggerSources.Schedule;
                foreach (var problem in own.Where(item => item.Kind == triggerKind && item.Id == part.Key))
                {
                    part.Refuse(problem.Code switch
                    {
                        NhAssistantRunConfigurationProblems.UnknownAgent => TaskResult.Failed(NhAssistantErrorCodes.AgentNotConfigured, "The agent has no run configuration."),
                        NhAssistantRunConfigurationProblems.ConflictsWithCode => TaskResult.Failed(NhAssistantErrorCodes.TriggerInCode, "The trigger is registered in code."),
                        _ => NhAssistantAgentConfiguration.TriggerFieldError(problem.Code, schedule)
                    });
                }

                break;
            case NhAssistantPackagePartKinds.Consultation:
                var codes = own
                    .Where(item => item.Kind == NhAssistantRunConfigurationProblems.ConsultationKind && item.Id == part.Key)
                    .Select(item => item.Code)
                    .ToArray();
                if (codes.Length > 0)
                {
                    part.Refuse(NhAssistantAgentConfiguration.ConsultationFieldErrors(codes));
                }

                break;
            case NhAssistantPackagePartKinds.HandOff:
                foreach (var problem in own.Where(item => item.Kind == NhAssistantRunConfigurationProblems.HandOffKind && item.Id == part.Key))
                {
                    part.Refuse(HandOffs.FieldErrorOf(problem.Code, part.AgentId, part.Key));
                }

                break;
        }
    }

    /// <summary>
    /// Whether a problem of kind <c>agent</c> belongs to the definition (its instructions asset or
    /// the live agent of a shadow variant) rather than to the run settings.
    /// </summary>
    private static bool IsDefinitionProblem(string code)
    {
        return code is NhAssistantRunConfigurationProblems.NoAssetSource
            or NhAssistantRunConfigurationProblems.AssetMissing
            or NhAssistantRunConfigurationProblems.AssetChanged
            or NhAssistantRunConfigurationProblems.AssetInvalid
            or NhAssistantRunConfigurationProblems.AssetUnavailable
            or NhAssistantRunConfigurationProblems.OutsideCeiling
            or NhAssistantRunConfigurationProblems.UnknownLiveAgent
            or NhAssistantRunConfigurationProblems.LiveAgentNotLive
            or NhAssistantRunConfigurationProblems.TooManyVariants;
    }

    private static TaskResult DefinitionFailure(string code)
    {
        return code switch
        {
            NhAssistantRunConfigurationProblems.OutsideCeiling => TaskResult.Failed(NhAssistantErrorCodes.ShadowVariantsNotAllowed, "The agent may not get shadow variants created as data."),
            NhAssistantRunConfigurationProblems.UnknownLiveAgent => NhAssistantValidationErrors.Failed("shadowOf", NhAssistantFieldErrors.NotFound),
            NhAssistantRunConfigurationProblems.LiveAgentNotLive => NhAssistantValidationErrors.Failed("shadowOf", NhAssistantFieldErrors.Invalid),
            NhAssistantRunConfigurationProblems.TooManyVariants => TaskResult.Failed(NhAssistantErrorCodes.ConfigurationLimit, "The agent has as many shadow variants created as data as allowed."),
            _ => AssetFailure(code)
        };
    }

    private static TaskResult SkillFailure(string code)
    {
        return code switch
        {
            NhAssistantRunConfigurationProblems.UnknownAgent => TaskResult.Failed(NhAssistantErrorCodes.AgentNotConfigured, "The agent has no run configuration."),
            NhAssistantRunConfigurationProblems.ConflictsWithCode => TaskResult.Failed(NhAssistantErrorCodes.SkillInCode, "The skill is declared in code."),
            NhAssistantRunConfigurationProblems.TooManySkills => TaskResult.Failed(NhAssistantErrorCodes.ConfigurationLimit, "The agent has as many skills as allowed."),
            NhAssistantRunConfigurationProblems.NoAssetSource
                or NhAssistantRunConfigurationProblems.AssetMissing
                or NhAssistantRunConfigurationProblems.AssetChanged
                or NhAssistantRunConfigurationProblems.AssetInvalid
                or NhAssistantRunConfigurationProblems.AssetUnavailable => AssetFailure(code),
            _ => NhAssistantAgentConfiguration.SkillFieldError(code)
        };
    }

    /// <summary>
    /// The answer of a save to an asset of the application that does not resolve.
    /// </summary>
    private static TaskResult AssetFailure(string code)
    {
        return code switch
        {
            NhAssistantRunConfigurationProblems.NoAssetSource => NhAssistantValidationErrors.Failed(NhAssistantInstructionAssets.AssetField, NhAssistantFieldErrors.NotAllowed),
            NhAssistantRunConfigurationProblems.AssetMissing => NhAssistantValidationErrors.Failed(NhAssistantInstructionAssets.AssetField, NhAssistantFieldErrors.NotFound),
            NhAssistantRunConfigurationProblems.AssetChanged => NhAssistantValidationErrors.Failed(NhAssistantInstructionAssets.AssetField + ".hash", NhAssistantFieldErrors.Invalid),
            NhAssistantRunConfigurationProblems.AssetUnavailable => TaskResult.Failed(NhAssistantAdminErrorCodes.InstructionsAssetUnavailable, "The application's text asset source failed."),
            _ => NhAssistantValidationErrors.Failed(NhAssistantInstructionAssets.AssetField, NhAssistantFieldErrors.Invalid)
        };
    }

    /// <summary>
    /// Refuses the new triggers, consultations and hand-offs of an agent that would have more than a
    /// save allows.
    /// </summary>
    private static void RefuseBeyondLimits(
        NhAssistantPackagePlan plan,
        IReadOnlyList<AssistantAgentTriggerDefinition> triggers,
        IReadOnlyList<AssistantAgentConsultationDefinition> consultations,
        IReadOnlyList<AssistantAgentHandOffDefinition> handOffs)
    {
        foreach (var agent in plan.Agents)
        {
            var limits = new (string Kind, int Count, int Max)[]
            {
                (NhAssistantPackagePartKinds.Trigger, triggers.Count(item => item.AgentId == agent.Id && item.Status == NhAssistantSkillDefinitionStatuses.Live), NhAssistantAgentConfigurationLimits.MaxTriggersPerAgent),
                (NhAssistantPackagePartKinds.Consultation, consultations.Count(item => item.AgentId == agent.Id), NhAssistantAgentConfigurationLimits.MaxConsultationsPerAgent),
                (NhAssistantPackagePartKinds.HandOff, handOffs.Count(item => item.AgentId == agent.Id), NhAssistantDataHandOffLimits.MaxHandOffsPerAgent)
            };
            foreach (var (kind, count, max) in limits.Where(item => item.Count > item.Max))
            {
                foreach (var part in plan.PartsOf(agent.Id, kind).Where(item => item.Outcome == NhAssistantPackagePartOutcomes.Created))
                {
                    part.Refuse(TaskResult.Failed(NhAssistantErrorCodes.ConfigurationLimit, "The agent has as many of these parts as allowed."));
                }
            }
        }
    }

    /// <summary>
    /// The hash and the lockable fields every linked part has after the import.
    /// </summary>
    private static void PrepareLinks(NhAssistantPackagePlan plan)
    {
        foreach (var part in plan.Parts.Where(item => item.Linked && !item.Refused))
        {
            part.Hash = part.Row switch
            {
                AssistantAgent row when NhAssistantAgentPackageParts.DefinitionOf(row) is { } definition => NhAssistantAgentPackageFormat.HashOf(definition),
                AssistantAgentRunConfiguration row => NhAssistantAgentPackageFormat.HashOf(NhAssistantAgentPackageParts.RunSettingsOf(row)),
                AssistantAgentSkillDefinition row => NhAssistantAgentPackageFormat.HashOf(NhAssistantAgentPackageParts.SkillOf(row)),
                AssistantAgentTriggerDefinition row => NhAssistantAgentPackageFormat.HashOf(NhAssistantAgentPackageParts.TriggerOf(row)),
                AssistantAgentConsultationDefinition row => NhAssistantAgentPackageFormat.HashOf(NhAssistantAgentPackageParts.ConsultationOf(row)),
                AssistantAgentHandOffDefinition row => NhAssistantAgentPackageFormat.HashOf(NhAssistantAgentPackageParts.HandOffOf(row)),
                NhAssistantAgentMetadataCheck check when check.Json is not null => NhAssistantAgentPackageFormat.HashOf(NhAssistantAgentPackageParts.MetadataOf(check.Json)),
                _ => null
            };
            part.Fields ??= part.Row switch
            {
                AssistantAgent row => NhAssistantPackageFields.Of(row),
                _ => null
            };
        }
    }

    /// <summary>
    /// Writes every agent and part of the plan, its links and the package in one transaction with
    /// one change of the run configuration stamp, in dependency order: agents, then run settings,
    /// skills, triggers, consultations and hand-offs, then metadata of agents in code and the links.
    /// </summary>
    private async Task<bool> WriteAsync(NhAssistantPackagePlan plan, string actorId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var info = plan.Package.Package;
        var links = plan.Parts
            .Where(part => part.Linked && part.Hash is not null && part.Fields is not null)
            .Select(part => LinkOf(part, info, actorId, now))
            .ToArray();
        var unlinked = plan.Parts
            .Where(part => part.Outcome == NhAssistantPackagePartOutcomes.LeftBehind && part.Link is not null)
            .Select(part => (part.AgentId, part.Kind, part.Key))
            .ToList();
        foreach (var agentId in plan.LeftBehindAgents)
        {
            foreach (var link in await packages.ListLinksAsync(agentId, cancellationToken))
            {
                unlinked.Add((link.AgentId, link.PartKind, link.PartKey));
            }
        }

        if (plan.Promotion is { } promotion)
        {
            return await WritePromotionAsync(plan, promotion, links, unlinked, actorId, now, cancellationToken);
        }

        var package = new AssistantAgentPackage
        {
            Id = info.Id,
            Version = info.Version,
            FormatVersion = plan.Package.FormatVersion,
            Hash = plan.Hash,
            ImportedAt = now,
            ImportedBy = actorId
        };
        var outcome = await packages.ImportAsync(
            (context, token) => WritePartsAsync(context, plan, actorId, now, token),
            package,
            plan.Agents.Select(agent => agent.Id).ToArray(),
            links,
            unlinked,
            plan.Installed?.Version,
            cancellationToken);
        return outcome == NhAssistantStoreOutcome.Succeeded;
    }

    /// <summary>
    /// Writes a promotion of a package-linked variant (FB11) in the one transaction of an import: the
    /// variant goes first with everything that belongs to it, its links included, so its trigger ids
    /// are free; the live agent leaves the package it belonged to before; then the live agent's parts
    /// and links are written as an import writes them. The package keeps its version, hash and import,
    /// and holds the live agent in place of the variant.
    /// </summary>
    private async Task<bool> WritePromotionAsync(
        NhAssistantPackagePlan plan,
        NhAssistantPackagePromotion promotion,
        IReadOnlyList<AssistantAgentPackageLink> links,
        List<(string AgentId, string PartKind, string PartKey)> unlinked,
        string actorId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var installed = plan.Installed!;
        // The live agent's links to the package it belonged to before end with the promotion.
        foreach (var link in await packages.ListLinksAsync(promotion.LiveAgentId, cancellationToken))
        {
            if (link.PackageId != installed.Id)
            {
                unlinked.Add((link.AgentId, link.PartKind, link.PartKey));
            }
        }

        var package = new AssistantAgentPackage
        {
            Id = installed.Id,
            Version = installed.Version,
            FormatVersion = installed.FormatVersion,
            Hash = installed.Hash,
            ImportedAt = installed.ImportedAt,
            ImportedBy = installed.ImportedBy
        };
        var agentIds = NhAssistantAgentPackageStore.AgentIdsOf(installed)
            .Where(id => !string.Equals(id, promotion.VariantId, StringComparison.Ordinal))
            .Append(promotion.LiveAgentId)
            .ToArray();
        var outcome = await packages.ImportAsync(
            async (context, token) =>
            {
                if (!await NhAssistantAgentPackageStore.PromoteAsync(context, promotion.VariantId, promotion.VariantVersion, promotion.LiveAgentId, installed.Id, token))
                {
                    return false;
                }

                return await WritePartsAsync(context, plan, actorId, now, token);
            },
            package,
            agentIds,
            links,
            unlinked,
            installed.Version,
            cancellationToken);
        return outcome == NhAssistantStoreOutcome.Succeeded;
    }

    private async Task<bool> WritePartsAsync(
        NhAssistantDbContext context,
        NhAssistantPackagePlan plan,
        string actorId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        foreach (var agent in plan.Agents)
        {
            if (!await WriteAgentAsync(context, plan, agent, actorId, now, cancellationToken))
            {
                return false;
            }
        }

        foreach (var part in plan.Parts.Where(item => item.Writes))
        {
            switch (part.Row)
            {
                case AssistantAgentRunConfiguration settings:
                    var savedSettings = await NhAssistantRunConfigurationStore.SaveRunSettingsAsync(context, settings, part.ExpectedVersion, cancellationToken);
                    if (!savedSettings.Succeeded)
                    {
                        return false;
                    }

                    part.SavedVersion = savedSettings.Value!.Version;
                    break;
                case AssistantAgentSkillDefinition skill:
                    var savedSkill = await NhAssistantRunConfigurationStore.AddSkillVersionAsync(context, skill, part.ExpectedVersion, part.AsCandidate, cancellationToken);
                    if (!savedSkill.Succeeded)
                    {
                        return false;
                    }

                    part.SavedVersion = savedSkill.Value!.Version;
                    if (part.StartsOff)
                    {
                        // Off in the same transaction, so no trigger reaches the new skill first.
                        context.AgentSkillStates.Add(new AssistantAgentSkillState
                        {
                            AgentId = part.AgentId,
                            SkillId = part.Key,
                            RunState = NhAssistantAgentRunStates.Off,
                            StateChangedAt = now,
                            StateChangedBy = actorId,
                            StateReason = skill.Reason
                        });
                        await context.SaveChangesAsync(cancellationToken);
                    }

                    break;
                case AssistantAgentTriggerDefinition trigger:
                    var savedTrigger = await NhAssistantRunConfigurationStore.SaveTriggerAsync(context, trigger, part.ExpectedVersion, part.AsCandidate, cancellationToken);
                    if (!savedTrigger.Succeeded)
                    {
                        return false;
                    }

                    part.SavedVersion = savedTrigger.Value!.Version;
                    break;
                case AssistantAgentConsultationDefinition consultation:
                    var savedConsultation = await NhAssistantRunConfigurationStore.SaveConsultationAsync(context, consultation, part.ExpectedVersion, cancellationToken);
                    if (!savedConsultation.Succeeded)
                    {
                        return false;
                    }

                    part.SavedVersion = savedConsultation.Value!.Version;
                    break;
                case AssistantAgentHandOffDefinition handOff:
                    var savedHandOff = await NhAssistantRunConfigurationStore.SaveHandOffAsync(context, handOff, part.ExpectedVersion, cancellationToken);
                    if (!savedHandOff.Succeeded)
                    {
                        return false;
                    }

                    part.SavedVersion = savedHandOff.Value!.Version;
                    break;
            }
        }

        return true;
    }

    /// <summary>
    /// Writes one agent: a new agent with its metadata and groups, or the change of its definition
    /// and metadata, as a create and an update store them.
    /// </summary>
    private async Task<bool> WriteAgentAsync(
        NhAssistantDbContext context,
        NhAssistantPackagePlan plan,
        NhAssistantPlannedAgent agent,
        string actorId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var definition = plan.PartsOf(agent.Id, NhAssistantPackagePartKinds.Agent).First();
        var metadata = plan.PartsOf(agent.Id, NhAssistantPackagePartKinds.Metadata).Where(part => part.Writes).ToArray();
        if (agent.HostRow is null)
        {
            if (!await NhAssistantAdminStore.AddAgentAsync(context, agent.Row!, [], agent.NewMetadataRows, cancellationToken))
            {
                return false;
            }

            foreach (var group in agent.Source.Groups ?? [])
            {
                context.AgentGroupMembers.Add(new AssistantAgentGroupMember
                {
                    AgentId = agent.Id,
                    GroupId = group,
                    IsMember = true,
                    ChangedAt = now,
                    ChangedBy = actorId
                });
            }

            await context.SaveChangesAsync(cancellationToken);
            return true;
        }

        if (agent.InCode)
        {
            if (definition.Writes)
            {
                var overridden = await NhAssistantAdminStore.UpdateAgentAsync(
                    context,
                    agent.Id,
                    agent.HostRow.Version,
                    row => Administration.ApplyChange(row, agent.Input!, actorId),
                    null,
                    null,
                    null,
                    cancellationToken);
                if (!overridden.Succeeded)
                {
                    return false;
                }

                agent.UpdatedRow = overridden.Value;
            }

            // An agent in code keeps an override per editable entry, with a version of its own.
            foreach (var part in metadata)
            {
                var saved = await NhAssistantAgentMetadataStore.SaveOverrideAsync(
                    context,
                    agent.Id,
                    part.Key,
                    part.ExpectedVersion,
                    (NhAssistantAgentMetadataCheck)part.Row!,
                    actorId,
                    now,
                    cancellationToken);
                if (!saved.Succeeded)
                {
                    return false;
                }

                part.SavedVersion = saved.Value!.Version;
                agent.MetadataChanges.Add(new NhAssistantAgentMetadataChange(
                    part.Key,
                    saved.Value.Version == 1 ? NhAssistantAgentMetadataStore.Created : NhAssistantAgentMetadataStore.Changed,
                    saved.Value.Version));
            }

            return true;
        }

        if (!definition.Writes && metadata.Length == 0)
        {
            return true;
        }

        // The metadata of an agent created in the administration changes with its next version.
        var values = metadata.ToDictionary(part => part.Key, part => (NhAssistantAgentMetadataCheck?)part.Row, StringComparer.Ordinal);
        var updated = await NhAssistantAdminStore.UpdateAgentAsync(
            context,
            agent.Id,
            agent.HostRow.Version,
            row =>
            {
                if (definition.Writes)
                {
                    Administration.ApplyChange(row, agent.Input!, actorId);
                }
                else
                {
                    row.UpdatedAt = now;
                    row.UpdatedBy = actorId;
                }
            },
            null,
            values,
            agent.MetadataChanges,
            cancellationToken);
        if (!updated.Succeeded)
        {
            return false;
        }

        agent.UpdatedRow = updated.Value;
        return true;
    }

    /// <summary>
    /// The link of a part after the import: the package, the hash of the part as the host has it, and
    /// the locked fields with the package's values.
    /// </summary>
    private static AssistantAgentPackageLink LinkOf(
        NhAssistantPlannedPart part,
        NhAssistantAgentPackageInfo info,
        string actorId,
        DateTimeOffset now)
    {
        var lockedFields = part.LockedFields;
        var all = lockedFields.Count == 1 && lockedFields[0] == NhAssistantPackageLockFields.All;
        var lockedValues = lockedFields.Count == 0
            ? null
            : part.Fields!
                .Where(item => all || lockedFields.Contains(item.Key, StringComparer.Ordinal))
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        return new AssistantAgentPackageLink
        {
            AgentId = part.AgentId,
            PartKind = part.Kind,
            PartKey = part.Key,
            PackageId = info.Id,
            PackageVersion = info.Version,
            Hash = part.Hash!,
            LockedFieldsJson = lockedFields.Count == 0 ? null : JsonSerializer.Serialize(lockedFields),
            LockedValuesJson = lockedValues is null ? null : JsonSerializer.Serialize(lockedValues),
            LinkedAt = now,
            LinkedBy = actorId
        };
    }

    /// <summary>
    /// Audits every part the import wrote as its save audits it, every agent and part left behind,
    /// and then the package (PK6, PK12); the technical account answers for the threads of an upgraded
    /// agent, as a save of run settings makes it.
    /// </summary>
    private async Task AuditImportAsync(NhAssistantPackagePlan plan, string actorId, CancellationToken cancellationToken)
    {
        var info = plan.Package.Package;
        await AuditWrittenAsync(plan, actorId, cancellationToken);
        foreach (var part in plan.Parts.Where(item => item.Outcome == NhAssistantPackagePartOutcomes.LeftBehind))
        {
            await events.PackageUnlinkedAsync(
                part.AgentId,
                plan.LeftBehindAgents.Contains(part.AgentId, StringComparer.Ordinal) ? NhAssistantPackagePartKinds.WholeAgent : part.Part,
                info.Id,
                info.Version,
                NhAssistantAgentPackageAudit.LeftBehind,
                actorId,
                cancellationToken);
        }

        await events.PackageImportedAsync(
            info.Id,
            info.Version,
            plan.Hash,
            plan.Agents.Count,
            plan.Installed is null ? NhAssistantAgentPackageAudit.Created : NhAssistantAgentPackageAudit.Upgraded,
            actorId,
            cancellationToken);
    }

    /// <summary>
    /// Audits every agent and part the plan wrote as its save audits it.
    /// </summary>
    private async Task AuditWrittenAsync(NhAssistantPackagePlan plan, string actorId, CancellationToken cancellationToken)
    {
        foreach (var agent in plan.Agents)
        {
            if (agent.HostRow is null)
            {
                await Administration.AuditCreatedAsync(agent.Row!, agent.NewMetadataRows, actorId, cancellationToken);
            }
            else if (agent.UpdatedRow is { } updated)
            {
                await Administration.AuditUpdatedAsync(updated, agent.MetadataChanges, actorId, cancellationToken);
            }
            else if (agent.MetadataChanges.Count > 0)
            {
                // The overrides of an agent in code change without a new version of the agent.
                await services.GetRequiredService<NhAssistantAgentMetadataService>().AuditAsync(agent.Id, agent.MetadataChanges, actorId, cancellationToken);
            }

            foreach (var part in plan.Parts.Where(item => item.AgentId == agent.Id && item.Writes))
            {
                await AuditPartAsync(agent, part, actorId, cancellationToken);
            }
        }
    }

    private async Task AuditPartAsync(NhAssistantPlannedAgent agent, NhAssistantPlannedPart part, string actorId, CancellationToken cancellationToken)
    {
        var change = part.ExpectedVersion is null ? "created" : "changed";
        switch (part.Row)
        {
            case AssistantAgentRunConfiguration:
                await events.RunSettingsChangedAsync(part.AgentId, part.SavedVersion ?? 1, change, actorId, cancellationToken);
                if (part.ServiceAccount is { } account && agent.HostRow is not null)
                {
                    // The agent's threads answer to the account, unless an administrator replaced it at runtime.
                    var current = await catalog.FindAsync(part.AgentId, includeDisabled: true, cancellationToken);
                    await runs.SetServiceAccountOfThreadsAsync(part.AgentId, current?.ServiceAccountOverride ?? account, cancellationToken);
                }

                break;
            case AssistantAgentSkillDefinition skill:
                await events.SkillDefinitionChangedAsync(
                    part.AgentId,
                    part.Key,
                    part.SavedVersion ?? skill.Version,
                    skill.InstructionsHash,
                    part.AsCandidate ? "candidate" : change,
                    actorId,
                    cancellationToken,
                    NhAssistantInstructionAssets.ReferenceOf(skill)?.InstructionsVersion);
                if (part.StartsOff)
                {
                    await events.AgentStateChangedAsync(part.AgentId, actorId, NhAssistantAgentRunStates.Off, 0, cancellationToken, part.Key);
                }

                break;
            case AssistantAgentTriggerDefinition trigger:
                await events.TriggerDefinitionChangedAsync(
                    part.AgentId,
                    part.Key,
                    trigger.SkillId,
                    part.SavedVersion ?? trigger.Version,
                    part.AsCandidate ? "candidate" : change,
                    actorId,
                    cancellationToken);
                break;
            case AssistantAgentConsultationDefinition consultation:
                var version = part.SavedVersion ?? consultation.Version;
                await events.ConsultationDefinitionChangedAsync(part.AgentId, part.Key, version, consultation.InstructionsHash, change, actorId, cancellationToken);
                if (version == 1 && consultation.Off)
                {
                    await events.ConsultationDefinitionChangedAsync(part.AgentId, part.Key, 1, consultation.InstructionsHash, NhAssistantConsultationStates.Off, actorId, cancellationToken);
                }

                break;
            case AssistantAgentHandOffDefinition:
                await events.HandOffDefinitionChangedAsync(part.AgentId, part.Key, part.SavedVersion ?? 1, change, actorId, cancellationToken);
                break;
        }
    }
}
