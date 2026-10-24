using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// The plan of an import: every part checked as its save checks it, then the whole package once
/// against the host as it would be after the import (PK17).
/// </summary>
internal sealed partial class NhAssistantAgentPackageService
{
    private NhAssistantAgentConfiguration Configuration => services.GetRequiredService<NhAssistantAgentConfiguration>();

    private NhAssistantDataHandOffs HandOffs => services.GetRequiredService<NhAssistantDataHandOffs>();

    private NhAssistantAgentAdministration Administration => services.GetRequiredService<NhAssistantAgentAdministration>();

    private NhAssistantRunConfigurationValidator Validator => services.GetRequiredService<NhAssistantRunConfigurationValidator>();

    private async Task<(TaskResult? Failure, NhAssistantPackagePlan? Plan)> PlanAsync(
        NhAssistantAgentPackage package,
        NhAssistantAgentPackageImportOptions options,
        string actorId,
        CancellationToken cancellationToken)
    {
        var checkedPackage = NhAssistantAgentPackageFormat.Check(package);
        if (!checkedPackage.Success)
        {
            return (checkedPackage, null);
        }
        if (Encoding.UTF8.GetByteCount(NhAssistantAgentPackageFormat.Write(package)) > NhAssistantAgentPackageFormat.MaxBytes)
        {
            return (TaskResult.Failed(NhAssistantErrorCodes.PackageTooLarge, "The agent package is larger than 1 MB or holds more than 20 agents."), null);
        }
        if (ValidatePackage(package, options) is { } invalid)
        {
            return (invalid, null);
        }

        await catalog.ListAsync(includeDisabled: true, cancellationToken);
        await configurations.EnsureCurrentAsync(cancellationToken);
        var installed = await packages.FindPackageAsync(package.Package.Id, cancellationToken);
        if (installed is not null && package.Package.Version < installed.Version)
        {
            // A package never goes back to an older version on this host.
            return (NhAssistantValidationErrors.Failed("package.version", NhAssistantFieldErrors.Invalid), null);
        }

        var installedAgents = installed is null ? [] : NhAssistantAgentPackageStore.AgentIdsOf(installed);
        var stored = await runConfiguration.LoadAsync(cancellationToken);
        var agentIds = package.Agents.Select(agent => agent.Id).Concat(installedAgents).Distinct(StringComparer.Ordinal).ToArray();
        var allLinks = await packages.ListLinksAsync(agentIds, cancellationToken);
        var plan = new NhAssistantPackagePlan(package, NhAssistantAgentPackageFormat.HashOf(package), installed);
        var reason = "package " + package.Package.Id + "@" + package.Package.Version.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var source in package.Agents)
        {
            await PlanAgentAsync(plan, source, options, stored, allLinks, installedAgents, actorId, reason, cancellationToken);
        }

        // An agent an upgrade no longer contains stays, unlinked (PK5).
        foreach (var agentId in installedAgents.Where(id => package.Agents.All(agent => agent.Id != id)))
        {
            plan.LeftBehindAgents.Add(agentId);
            plan.Parts.Add(new NhAssistantPlannedPart(agentId, NhAssistantPackagePartKinds.Agent, string.Empty)
            {
                Outcome = NhAssistantPackagePartOutcomes.LeftBehind
            });
        }

        await CheckAgainstHostAsync(plan, stored, cancellationToken);
        PrepareLinks(plan);
        return (null, plan);
    }

    /// <summary>
    /// The checks of the package and the options that need nothing of the host.
    /// </summary>
    private static TaskResult? ValidatePackage(NhAssistantAgentPackage package, NhAssistantAgentPackageImportOptions options)
    {
        var info = package.Package;
        var ids = package.Agents.Select(agent => agent.Id).ToArray();
        var errors = new NhAssistantValidationErrors()
            .Require(
                info.Id is not null && NhAssistantNames.IsSegment(info.Id) && info.Id.Length <= NhAssistantAgentPackageFormat.MaxIdLength,
                "package.id",
                NhAssistantFieldErrors.Invalid)
            .Require(info.Version >= 1, "package.version", NhAssistantFieldErrors.Invalid)
            .Require(info.Description is not { Length: > NhAssistantAgentPackageFormat.MaxDescriptionLength }, "package.description", NhAssistantFieldErrors.TooLong)
            .Require(ids.Length > 0, "agents", NhAssistantFieldErrors.Required)
            .Require(
                ids.All(id => id is not null && NhAssistantNames.IsSegment(id))
                && ids.Distinct(StringComparer.Ordinal).Count() == ids.Length
                && package.Agents.All(agent => agent.Source is NhAssistantAgentSources.Admin or NhAssistantAgentSources.Code),
                "agents",
                NhAssistantFieldErrors.Invalid)
            .Require(options.Apply is null or NhAssistantSkillApplyModes.Live or NhAssistantSkillApplyModes.Shadow, "apply", NhAssistantFieldErrors.Invalid)
            .Require(
                options.ServiceAccounts is null
                || options.ServiceAccounts.All(item => ids.Contains(item.Key, StringComparer.Ordinal)
                                                       && !string.IsNullOrWhiteSpace(item.Value)
                                                       && item.Value.Trim().Length <= MaxActorIdLength),
                "serviceAccounts",
                NhAssistantFieldErrors.Invalid)
            .Require(
                options.Relink is null
                || options.Relink.All(item => item is not null
                                              && ids.Contains(item.AgentId, StringComparer.Ordinal)
                                              && (item.Part is null || NhAssistantPackagePartKinds.TryParse(item.Part, out _, out _))),
                "relink",
                NhAssistantFieldErrors.Invalid);
        return errors.IsEmpty ? null : errors.ToResult();
    }

    private async Task PlanAgentAsync(
        NhAssistantPackagePlan plan,
        NhAssistantPackageAgent source,
        NhAssistantAgentPackageImportOptions options,
        NhAssistantStoredRunConfiguration stored,
        IReadOnlyList<AssistantAgentPackageLink> allLinks,
        IReadOnlyList<string> installedAgents,
        string actorId,
        string reason,
        CancellationToken cancellationToken)
    {
        var packageId = plan.Package.Package.Id;
        var hostRow = await admin.FindAgentAsync(source.Id, cancellationToken);
        var agent = new NhAssistantPlannedAgent(source, hostRow)
        {
            Member = installedAgents.Contains(source.Id, StringComparer.Ordinal) || Relinks(options, source.Id, null),
            Links = allLinks.Where(link => link.AgentId == source.Id && link.PackageId == packageId).ToArray(),
            HostMetadata = hostRow is null ? [] : await metadataStore.ListAsync(source.Id, cancellationToken)
        };
        plan.Agents.Add(agent);

        var agentPart = new NhAssistantPlannedPart(source.Id, NhAssistantPackagePartKinds.Agent, string.Empty);
        plan.Parts.Add(agentPart);
        await PlanDefinitionAsync(agent, agentPart, options, allLinks, packageId, actorId, cancellationToken);
        PlanMetadata(plan, agent, options);
        await PlanRunSettingsAsync(plan, agent, options, stored, actorId, reason, cancellationToken);
        await PlanSkillsAsync(plan, agent, options, stored, actorId, reason, cancellationToken);
        PlanTriggers(plan, agent, options, stored, actorId, reason);
        PlanConsultations(plan, agent, options, stored, actorId, reason);
        PlanHandOffs(plan, agent, options, stored, actorId, reason);

        // A part an earlier version linked that this version no longer contains stays, unlinked.
        var carried = PartsOf(source);
        foreach (var link in agent.Links)
        {
            var part = NhAssistantPackagePartKinds.PartOf(link.PartKind, link.PartKey);
            if (!carried.Contains(part, StringComparer.Ordinal) && !(link.PartKind == NhAssistantPackagePartKinds.Agent && agentPart.Linked))
            {
                plan.Parts.Add(new NhAssistantPlannedPart(source.Id, link.PartKind, link.PartKey)
                {
                    Outcome = NhAssistantPackagePartOutcomes.LeftBehind,
                    Link = link
                });
            }
        }

        CheckLocksAndHashes(plan, agent, agentPart);
    }

    private async Task PlanDefinitionAsync(
        NhAssistantPlannedAgent agent,
        NhAssistantPlannedPart part,
        NhAssistantAgentPackageImportOptions options,
        IReadOnlyList<AssistantAgentPackageLink> allLinks,
        string packageId,
        string actorId,
        CancellationToken cancellationToken)
    {
        var source = agent.Source;
        var hostRow = agent.HostRow;
        part.Link = agent.Links.FirstOrDefault(link => link.PartKind == NhAssistantPackagePartKinds.Agent);
        if (allLinks.Any(link => link.AgentId == source.Id && link.PackageId != packageId))
        {
            // The agent belongs to another package.
            part.Refuse(TaskResult.Failed(NhAssistantAdminErrorCodes.AgentExists, "An agent with this id already exists."));
            return;
        }

        if (agent.InCode)
        {
            if (!registration.TryGetAgent(source.Id, out _) || hostRow is null || hostRow.Source != NhAssistantAgentSources.Code)
            {
                // The import requires the same agent in code (PK2).
                part.Refuse(TaskResult.Failed(NhAssistantErrorCodes.AgentNotFound, "The agent in code was not found."));
                return;
            }
            if (source.RunSettings is not null)
            {
                part.Refuse("runSettings", NhAssistantFieldErrors.NotAllowed);
            }
            if (source.Definition is not { } overrides)
            {
                part.Current = hostRow;
                part.Row = null;
                part.Outcome = NhAssistantPackagePartOutcomes.Unchanged;
                agent.Row = hostRow;
                return;
            }
            if (KeptLocal(agent, part, exists: true, options))
            {
                agent.Row = hostRow;
                return;
            }

            var codeAutonomyValid = NhAssistantAgentPackageParts.TryReadAutonomy(overrides.Autonomy, out var codeAutonomy);
            var codeInput = NhAssistantAgentPackageParts.CodeInputOf(hostRow, overrides, codeAutonomyValid, codeAutonomy);
            if (await RequiredPolicyMissingAsync(codeInput.RequiredPolicy))
            {
                part.Refuse("requiredPolicy", NhAssistantFieldErrors.NotFound);
            }

            var (codeValidation, _) = await Administration.PrepareChangeAsync(codeInput, cancellationToken);
            if (!codeValidation.Success)
            {
                part.Refuse(codeValidation);
            }
            if (part.Refused)
            {
                return;
            }

            var overridden = (await admin.FindAgentAsync(source.Id, cancellationToken))!;
            Administration.ApplyChange(overridden, codeInput, actorId);
            agent.Input = codeInput;
            agent.Row = overridden;
            Decide(agent, part, hostRow, overridden, NhAssistantPackageFields.Of(hostRow), NhAssistantPackageFields.Of(overridden), [HashOfDefinition(hostRow)], options);
            return;
        }

        if (registration.TryGetAgent(source.Id, out _) || hostRow?.Source == NhAssistantAgentSources.Code || (hostRow is not null && !agent.Member))
        {
            // Another agent with the id refuses the whole package (PK5).
            part.Refuse(TaskResult.Failed(NhAssistantAdminErrorCodes.AgentExists, "An agent with this id already exists."));
            return;
        }
        if (source.Definition is not { } definition)
        {
            part.Refuse("definition", NhAssistantFieldErrors.Required);
            return;
        }

        if (hostRow is not null && KeptLocal(agent, part, exists: true, options))
        {
            agent.Row = hostRow;
            return;
        }

        var autonomyValid = NhAssistantAgentPackageParts.TryReadAutonomy(definition.Autonomy, out var autonomy);
        if (definition.Autonomy is null)
        {
            part.Refuse("autonomy", NhAssistantFieldErrors.Required);
        }

        // The metadata parts are checked one by one; a new agent stores them with its first version.
        var metadataValid = (source.Metadata ?? new Dictionary<string, JsonElement>()).All(item =>
            registration.AgentMetadata.TryGetValue(item.Key, out var entry)
            && item.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            && entry.Check(item.Value).Valid);
        var input = NhAssistantAgentPackageParts.AdminInputOf(source.Id, definition, metadataValid ? source.Metadata : null, autonomyValid, autonomy);
        if (await RequiredPolicyMissingAsync(input.RequiredPolicy))
        {
            part.Refuse("requiredPolicy", NhAssistantFieldErrors.NotFound);
        }

        CheckGroups(part, source, definition);
        agent.Input = input;
        if (hostRow is null)
        {
            var (failure, row, metadataRows) = await Administration.PrepareNewAgentAsync(input, actorId, checkShadowVariant: false, cancellationToken);
            if (failure is not null)
            {
                part.Refuse(failure);
                return;
            }
            if (part.Refused)
            {
                return;
            }

            agent.Row = row;
            agent.NewMetadataRows = metadataRows;
            part.Row = row;
            part.Outcome = NhAssistantPackagePartOutcomes.Created;
            return;
        }

        // An upgrade changes the definition; whether the agent starts off or in shadow was decided
        // when it was created. Its metadata parts are written with it.
        var change = input with { StartsOff = false, StartsInShadow = false, Metadata = null };
        var (validation, _) = await Administration.PrepareChangeAsync(change, cancellationToken);
        if (!validation.Success)
        {
            part.Refuse(validation);
            return;
        }
        if (part.Refused)
        {
            return;
        }

        var changed = (await admin.FindAgentAsync(source.Id, cancellationToken))!;
        Administration.ApplyChange(changed, change, actorId);
        // A variant keeps the live agent it was created for.
        changed.ShadowOfAgentId = hostRow.ShadowOfAgentId;
        agent.Input = change;
        agent.Row = changed;
        Decide(agent, part, hostRow, changed, NhAssistantPackageFields.Of(hostRow), NhAssistantPackageFields.Of(changed), [HashOfDefinition(hostRow)], options);
        if (!string.Equals(definition.ShadowOf, hostRow.ShadowOfAgentId, StringComparison.Ordinal) && definition.ShadowOf is not null)
        {
            part.Refuse("shadowOf", NhAssistantFieldErrors.Invalid);
        }
    }

    /// <summary>
    /// The groups of a new agent: registered, at most the most an agent may be in, none for a
    /// shadow variant. Groups are free (PK9): an upgrade leaves the host's groups as they are.
    /// </summary>
    private void CheckGroups(NhAssistantPlannedPart part, NhAssistantPackageAgent source, NhAssistantPackageAgentDefinition definition)
    {
        var groups = source.Groups ?? [];
        if (groups.Count == 0)
        {
            return;
        }
        if (groups.Count > NhAssistantAgentGroupDefinition.MaxGroups)
        {
            part.Refuse("groups", NhAssistantFieldErrors.TooLong);
        }
        if (groups.Any(group => group is null || !registration.AgentGroups.ContainsKey(group)))
        {
            part.Refuse("groups", NhAssistantFieldErrors.NotFound);
        }
        if (definition.ShadowOf is not null)
        {
            part.Refuse(TaskResult.Failed(NhAssistantErrorCodes.ShadowVariant, "A shadow variant works for administrators only; boards show live agents."));
        }
    }

    private void PlanMetadata(NhAssistantPackagePlan plan, NhAssistantPlannedAgent agent, NhAssistantAgentPackageImportOptions options)
    {
        foreach (var (name, value) in (agent.Source.Metadata ?? new Dictionary<string, JsonElement>()).OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            var part = new NhAssistantPlannedPart(agent.Id, NhAssistantPackagePartKinds.Metadata, name)
            {
                Link = agent.Links.FirstOrDefault(link => link.PartKind == NhAssistantPackagePartKinds.Metadata && link.PartKey == name)
            };
            plan.Parts.Add(part);
            var current = agent.HostMetadata.FirstOrDefault(row => row.Name == name);
            part.Current = current;
            if (!registration.AgentMetadata.TryGetValue(name, out var entry))
            {
                part.Refuse(TaskResult.Failed(NhAssistantAdminErrorCodes.MetadataEntryNotFound, "No agent metadata entry with this name is registered."));
                continue;
            }
            if (KeptLocal(agent, part, current is not null, options))
            {
                continue;
            }
            if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                // A package carries values; it never removes one.
                part.Refuse("value", NhAssistantFieldErrors.Required);
                continue;
            }
            if (agent.InCode && !entry.Editable)
            {
                part.Refuse(TaskResult.Failed(NhAssistantAdminErrorCodes.MetadataReadOnly, "The entry of an agent in code is set in code."));
                continue;
            }

            var check = entry.Check(value);
            if (!check.Valid)
            {
                var errors = new NhAssistantValidationErrors();
                check.AddTo(errors, "value");
                part.Refuse(errors.ToResult());
                continue;
            }

            part.Row = check;
            part.ExpectedVersion = current?.Version;
            Decide(
                agent,
                part,
                current,
                check,
                current is null ? null : NhAssistantPackageFields.OfMetadata(current.ValueJson),
                NhAssistantPackageFields.OfMetadata(check.Json),
                current is null ? [] : [NhAssistantAgentPackageFormat.HashOf(NhAssistantAgentPackageParts.MetadataOf(current.ValueJson))],
                options);
        }
    }

    private async Task PlanRunSettingsAsync(
        NhAssistantPackagePlan plan,
        NhAssistantPlannedAgent agent,
        NhAssistantAgentPackageImportOptions options,
        NhAssistantStoredRunConfiguration stored,
        string actorId,
        string reason,
        CancellationToken cancellationToken)
    {
        if (agent.InCode || agent.Source.RunSettings is not { } settings)
        {
            return;
        }

        var part = new NhAssistantPlannedPart(agent.Id, NhAssistantPackagePartKinds.RunSettings, string.Empty)
        {
            Link = agent.Links.FirstOrDefault(link => link.PartKind == NhAssistantPackagePartKinds.RunSettings)
        };
        plan.Parts.Add(part);
        var current = agent.HostRow is null ? null : stored.AgentRuns.FirstOrDefault(item => item.AgentId == agent.Id);
        part.Current = current;
        if (KeptLocal(agent, part, current is not null, options))
        {
            return;
        }

        // The technical account never travels (PK4): the import names it, or an upgrade keeps the host's.
        var serviceAccount = options.ServiceAccounts?.GetValueOrDefault(agent.Id)?.Trim();
        if (settings.Accountability == NhAssistantAccountabilities.ServiceAccount)
        {
            serviceAccount ??= current?.ServiceAccountActorId;
        }
        else
        {
            serviceAccount = null;
        }

        var request = NhAssistantAgentPackageParts.RequestOf(settings, serviceAccount, current?.Version, reason);
        if (Configuration.PrepareRunSettings(agent.Id, request, actorId, out var row) is { } refused)
        {
            part.Refuse(refused);
            return;
        }
        if (serviceAccount is not null && !await Configuration.AccountExistsAsync(agent.Id, serviceAccount, cancellationToken))
        {
            part.Refuse(TaskResult.Failed(NhAssistantErrorCodes.ServiceAccountNotFound, "No account that may sign in exists with that id."));
            return;
        }

        part.Row = row;
        part.ExpectedVersion = current?.Version;
        part.ServiceAccount = serviceAccount;
        Decide(
            agent,
            part,
            current,
            row,
            current is null ? null : NhAssistantPackageFields.Of(current),
            NhAssistantPackageFields.Of(row!),
            current is null ? [] : [NhAssistantAgentPackageFormat.HashOf(NhAssistantAgentPackageParts.RunSettingsOf(current))],
            options);
        if (part.Outcome == NhAssistantPackagePartOutcomes.Unchanged
            && !string.Equals(current?.ServiceAccountActorId, row!.ServiceAccountActorId, StringComparison.Ordinal))
        {
            // Another technical account is a change of the run settings, though not of the package.
            part.Outcome = NhAssistantPackagePartOutcomes.Changed;
        }
    }

    private async Task PlanSkillsAsync(
        NhAssistantPackagePlan plan,
        NhAssistantPlannedAgent agent,
        NhAssistantAgentPackageImportOptions options,
        NhAssistantStoredRunConfiguration stored,
        string actorId,
        string reason,
        CancellationToken cancellationToken)
    {
        var codeSkills = agent.InCode ? Validator.CodeAgent(agent.Id)?.CodeSkillIds ?? [] : [];
        foreach (var skill in agent.Source.Skills ?? [])
        {
            var part = new NhAssistantPlannedPart(agent.Id, NhAssistantPackagePartKinds.Skill, skill.Id ?? string.Empty)
            {
                Link = agent.Links.FirstOrDefault(link => link.PartKind == NhAssistantPackagePartKinds.Skill && link.PartKey == skill.Id)
            };
            plan.Parts.Add(part);
            var versions = stored.Skills.Where(item => item.AgentId == agent.Id && item.SkillId == skill.Id).ToArray();
            var current = agent.HostRow is null ? null : versions.FirstOrDefault(item => item.Status == NhAssistantSkillDefinitionStatuses.Live);
            part.Current = current;
            if (codeSkills.Contains(skill.Id, StringComparer.Ordinal))
            {
                part.Refuse(TaskResult.Failed(NhAssistantErrorCodes.SkillInCode, "The skill is declared in code."));
                continue;
            }
            if (KeptLocal(agent, part, current is not null, options))
            {
                continue;
            }

            var (refused, row, _) = await Configuration.PrepareSkillAsync(
                agent.Id,
                skill.Id ?? string.Empty,
                NhAssistantAgentPackageParts.RequestOf(skill, current?.Version, asCandidate: false, reason),
                actorId,
                cancellationToken);
            if (refused is not null)
            {
                part.Refuse(refused);
                continue;
            }

            part.Row = row;
            part.ExpectedVersion = current?.Version;
            part.StartsOff = current is null && skill.StartsOff == true;
            var hashes = versions
                .Where(item => item.Status is NhAssistantSkillDefinitionStatuses.Live or NhAssistantSkillDefinitionStatuses.Candidate)
                .Select(item => NhAssistantAgentPackageFormat.HashOf(NhAssistantAgentPackageParts.SkillOf(item)))
                .ToArray();
            Decide(agent, part, current, row, current is null ? null : NhAssistantPackageFields.Of(current), NhAssistantPackageFields.Of(row!), hashes, options);
            part.AsCandidate = part.Outcome == NhAssistantPackagePartOutcomes.Changed && options.Apply == NhAssistantSkillApplyModes.Shadow;
        }
    }

    private void PlanTriggers(
        NhAssistantPackagePlan plan,
        NhAssistantPlannedAgent agent,
        NhAssistantAgentPackageImportOptions options,
        NhAssistantStoredRunConfiguration stored,
        string actorId,
        string reason)
    {
        foreach (var trigger in agent.Source.Triggers ?? [])
        {
            var part = new NhAssistantPlannedPart(agent.Id, NhAssistantPackagePartKinds.Trigger, trigger.Id ?? string.Empty)
            {
                Link = agent.Links.FirstOrDefault(link => link.PartKind == NhAssistantPackagePartKinds.Trigger && link.PartKey == trigger.Id)
            };
            plan.Parts.Add(part);
            var versions = stored.Triggers.Where(item => item.Id == trigger.Id).ToArray();
            var current = versions.FirstOrDefault(item => item.AgentId == agent.Id && item.Status == NhAssistantSkillDefinitionStatuses.Live);
            part.Current = current;
            if (trigger.Id is not null && registration.CodeTriggers.ContainsKey(trigger.Id))
            {
                part.Refuse(TaskResult.Failed(NhAssistantErrorCodes.TriggerInCode, "The trigger is registered in code."));
                continue;
            }
            if (versions.Any(item => item.AgentId != agent.Id))
            {
                // Trigger ids stay (PK5): one that belongs to another agent is refused, as a save refuses it.
                part.Refuse("triggerId", NhAssistantFieldErrors.Invalid);
                continue;
            }
            if (KeptLocal(agent, part, current is not null, options))
            {
                continue;
            }

            if (Configuration.PrepareTrigger(
                    agent.Id,
                    trigger.Id ?? string.Empty,
                    NhAssistantAgentPackageParts.RequestOf(trigger, current?.Version, asCandidate: false, reason),
                    actorId,
                    out var row) is { } refused)
            {
                part.Refuse(refused);
                continue;
            }

            part.Row = row;
            part.ExpectedVersion = current?.Version;
            var hashes = versions
                .Where(item => item.Status is NhAssistantSkillDefinitionStatuses.Live or NhAssistantSkillDefinitionStatuses.Candidate)
                .Select(item => NhAssistantAgentPackageFormat.HashOf(NhAssistantAgentPackageParts.TriggerOf(item)))
                .ToArray();
            Decide(agent, part, current, row, current is null ? null : NhAssistantPackageFields.Of(current), NhAssistantPackageFields.Of(row!), hashes, options);
            part.AsCandidate = part.Outcome == NhAssistantPackagePartOutcomes.Changed && options.Apply == NhAssistantSkillApplyModes.Shadow;
        }
    }

    private void PlanConsultations(
        NhAssistantPackagePlan plan,
        NhAssistantPlannedAgent agent,
        NhAssistantAgentPackageImportOptions options,
        NhAssistantStoredRunConfiguration stored,
        string actorId,
        string reason)
    {
        foreach (var consultation in agent.Source.Consultations ?? [])
        {
            var part = new NhAssistantPlannedPart(agent.Id, NhAssistantPackagePartKinds.Consultation, consultation.SubjectType ?? string.Empty)
            {
                Link = agent.Links.FirstOrDefault(link => link.PartKind == NhAssistantPackagePartKinds.Consultation && link.PartKey == consultation.SubjectType)
            };
            plan.Parts.Add(part);
            var current = stored.Consultations.FirstOrDefault(item => item.AgentId == agent.Id && item.SubjectType == consultation.SubjectType);
            part.Current = current;
            if (registration.ConsultationCeilingOf(agent.Id) is not { } ceiling)
            {
                part.Refuse(TaskResult.Failed(NhAssistantErrorCodes.ConsultationsNotAllowed, "The agent may not get consultations defined as data."));
                continue;
            }
            if (registration.CodeAgentRuns.TryGetValue(agent.Id, out var code)
                && code.Consultations.Any(item => string.Equals(item.SubjectType, consultation.SubjectType, StringComparison.Ordinal)))
            {
                part.Refuse(TaskResult.Failed(NhAssistantErrorCodes.ConsultationInCode, "The agent allows consultation about this subject type in code."));
                continue;
            }
            if (KeptLocal(agent, part, current is not null, options))
            {
                continue;
            }

            if (Configuration.PrepareConsultation(
                    agent.Id,
                    consultation.SubjectType ?? string.Empty,
                    NhAssistantAgentPackageParts.RequestOf(consultation, current?.Version, reason),
                    actorId,
                    ceiling,
                    out var row) is { } refused)
            {
                part.Refuse(refused);
                continue;
            }

            part.Row = row;
            part.ExpectedVersion = current?.Version;
            Decide(
                agent,
                part,
                current,
                row,
                current is null ? null : NhAssistantPackageFields.Of(current),
                NhAssistantPackageFields.Of(row!),
                current is null ? [] : [NhAssistantAgentPackageFormat.HashOf(NhAssistantAgentPackageParts.ConsultationOf(current))],
                options);
        }
    }

    private void PlanHandOffs(
        NhAssistantPackagePlan plan,
        NhAssistantPlannedAgent agent,
        NhAssistantAgentPackageImportOptions options,
        NhAssistantStoredRunConfiguration stored,
        string actorId,
        string reason)
    {
        foreach (var handOff in agent.Source.HandOffs ?? [])
        {
            var part = new NhAssistantPlannedPart(agent.Id, NhAssistantPackagePartKinds.HandOff, handOff.SubjectType ?? string.Empty)
            {
                Link = agent.Links.FirstOrDefault(link => link.PartKind == NhAssistantPackagePartKinds.HandOff && link.PartKey == handOff.SubjectType)
            };
            plan.Parts.Add(part);
            var current = stored.HandOffs.FirstOrDefault(item => item.AgentId == agent.Id && item.SubjectType == handOff.SubjectType);
            part.Current = current;
            if (registration.HandOffsAsData is not { } ceiling || !ceiling.AllowsTarget(agent.Id, Validator.IsCreatedInAdministration))
            {
                part.Refuse(TaskResult.Failed(NhAssistantErrorCodes.HandOffsNotAllowed, "The agent may not accept hand-offs defined as data."));
                continue;
            }
            if (registration.CodeAgentRuns.TryGetValue(agent.Id, out var code)
                && code.HandOffs.Any(item => string.Equals(item.SubjectType, handOff.SubjectType, StringComparison.Ordinal)))
            {
                part.Refuse(TaskResult.Failed(NhAssistantErrorCodes.HandOffInCode, "The agent accepts hand-offs about this subject type in code."));
                continue;
            }
            if (KeptLocal(agent, part, current is not null, options))
            {
                continue;
            }

            if (HandOffs.Prepare(
                    agent.Id,
                    handOff.SubjectType ?? string.Empty,
                    NhAssistantAgentPackageParts.RequestOf(handOff, current?.Version, reason),
                    actorId,
                    out var row) is { } refused)
            {
                part.Refuse(refused);
                continue;
            }

            part.Row = row;
            part.ExpectedVersion = current?.Version;
            Decide(
                agent,
                part,
                current,
                row,
                current is null ? null : NhAssistantPackageFields.Of(current),
                NhAssistantPackageFields.Of(row!),
                current is null ? [] : [NhAssistantAgentPackageFormat.HashOf(NhAssistantAgentPackageParts.HandOffOf(current))],
                options);
        }
    }

    /// <summary>
    /// Whether the part stays as the host has it (PK10): the host has it without a link while the
    /// agent belongs to the package, because an administrator unlinked it, and the import does not
    /// relink it. A kept part is not checked, since nothing of it is imported.
    /// </summary>
    private static bool KeptLocal(NhAssistantPlannedAgent agent, NhAssistantPlannedPart part, bool exists, NhAssistantAgentPackageImportOptions options)
    {
        if (!exists || part.Link is not null || !agent.Member || Relinks(options, agent.Id, part.Part))
        {
            return false;
        }

        part.Outcome = NhAssistantPackagePartOutcomes.KeptLocal;
        return true;
    }

    /// <summary>
    /// The outcome of a part the import applies: <c>created</c> without one on the host, otherwise
    /// <c>unchanged</c> or <c>changed</c> by its fields, and whether the host changed it locally since
    /// the last import.
    /// </summary>
    private static void Decide(
        NhAssistantPlannedAgent agent,
        NhAssistantPlannedPart part,
        object? current,
        object? row,
        IReadOnlyDictionary<string, string>? currentFields,
        IReadOnlyDictionary<string, string> nextFields,
        IReadOnlyList<string> currentHashes,
        NhAssistantAgentPackageImportOptions options)
    {
        _ = agent;
        _ = options;
        if (part.Refused)
        {
            return;
        }

        part.Current = current;
        part.Row = row;
        part.Fields = nextFields;
        if (current is null || currentFields is null)
        {
            part.Outcome = NhAssistantPackagePartOutcomes.Created;
            return;
        }

        part.ChangedLocally = part.Link is not null && !currentHashes.Contains(part.Link.Hash, StringComparer.Ordinal);
        part.Outcome = currentFields.Count == nextFields.Count
                       && currentFields.All(item => nextFields.TryGetValue(item.Key, out var next) && string.Equals(next, item.Value, StringComparison.Ordinal))
            ? NhAssistantPackagePartOutcomes.Unchanged
            : NhAssistantPackagePartOutcomes.Changed;
    }

    private static bool Relinks(NhAssistantAgentPackageImportOptions options, string agentId, string? part)
    {
        return options.Relink?.Any(item => item is not null
                                           && item.AgentId == agentId
                                           && (item.Part is null || (part is not null && item.Part == part))) == true;
    }

    private async Task<bool> RequiredPolicyMissingAsync(string? policy)
    {
        return policy is not null
               && await services.GetRequiredService<IAuthorizationPolicyProvider>().GetPolicyAsync(policy) is null;
    }

    private static string HashOfDefinition(AssistantAgent row)
    {
        return NhAssistantAgentPackageParts.DefinitionOf(row) is { } definition
            ? NhAssistantAgentPackageFormat.HashOf(definition)
            : string.Empty;
    }

    /// <summary>
    /// Checks the package's locks and hashes of one agent: a lock names a part the agent carries and
    /// fields of its kind; a hash that is given is the hash of its part.
    /// </summary>
    private static void CheckLocksAndHashes(NhAssistantPackagePlan plan, NhAssistantPlannedAgent agent, NhAssistantPlannedPart agentPart)
    {
        var source = agent.Source;
        if (source.Locks is { Count: > 0 } locks)
        {
            var errors = new NhAssistantValidationErrors();
            CheckLocks(source, locks, errors, "locks");
            if (!errors.IsEmpty)
            {
                agentPart.Refuse(errors.ToResult());
            }

            foreach (var part in plan.Parts.Where(item => item.AgentId == agent.Id))
            {
                if (locks.TryGetValue(part.Part, out var fields) && fields is not null)
                {
                    part.LockedFields = fields.ToArray();
                }
            }
        }

        if (source.Hashes is not { Count: > 0 } given)
        {
            return;
        }

        var computed = HashesOf(source);
        foreach (var (part, hash) in given)
        {
            if (!computed.TryGetValue(part, out var expected))
            {
                agentPart.Refuse("hashes", NhAssistantFieldErrors.Invalid);
                continue;
            }
            if (!string.Equals(expected, hash, StringComparison.Ordinal))
            {
                var planned = plan.Parts.FirstOrDefault(item => item.AgentId == agent.Id && item.Part == part) ?? agentPart;
                planned.Refuse("hash", NhAssistantFieldErrors.Invalid);
            }
        }
    }
}
