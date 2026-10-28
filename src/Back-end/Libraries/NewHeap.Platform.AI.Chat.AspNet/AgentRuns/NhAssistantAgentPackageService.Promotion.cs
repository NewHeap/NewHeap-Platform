using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// The promotion of a shadow variant whose parts all belong to one agent package as that package's
/// next version of its live agent (FB11): the variant's parts, read as an export reads them, become a
/// package of the live agent under the variant's package id and version, which the import's own plan
/// checks and its one transaction writes, with the variant's deletion.
/// </summary>
internal sealed partial class NhAssistantAgentPackageService
{
    /// <summary>
    /// Applies the parts of <paramref name="variant"/> to <paramref name="live"/> with the import's
    /// rules and relinks the live agent to the variant's package version, then deletes the variant and
    /// its links, all in one transaction. Fails with <c>assistant-package-not-linked</c> when a part of
    /// the variant is not linked or its parts belong to more than one package,
    /// <c>instructionsAsset: not-allowed</c> for an asset onto an agent in code, and
    /// <c>assistant-package-refused</c> with the field error <c>parts: invalid</c> and
    /// <c>parts.{part}.{field}</c> per refused part when the import would refuse one; nothing changes
    /// then. The parts' own change events are audited as an import audits them; the caller audits the
    /// promotion. With <see cref="NhAssistantUncarriedParts.Relink"/> the live agent's linked parts the
    /// variant does not carry stay linked to the package version (FB33), as <c>kept-linked</c>.
    /// </summary>
    internal async Task<TaskResult<NhAssistantAgentPackageImportDto>> PromoteVariantAsync(
        NhAssistantAgent variant,
        NhAssistantAgent live,
        string actorId,
        string? reason,
        NhAssistantUncarriedParts uncarriedParts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(variant);
        ArgumentNullException.ThrowIfNull(live);
        ValidateActor(actorId);
        var variantRow = await admin.FindAgentAsync(variant.Id, cancellationToken);
        var liveRow = await admin.FindAgentAsync(live.Id, cancellationToken);
        if (variantRow is null || liveRow is null)
        {
            return TaskResult<NhAssistantAgentPackageImportDto>.Failed(NhAssistantErrorCodes.AgentNotFound, "The agent was not found.");
        }

        // Every part of the variant, as an export reads it, belongs to one package.
        var parts = await ExportAgentAsync(variantRow, cancellationToken);
        var links = await packages.ListLinksAsync(variant.Id, cancellationToken);
        var packageIds = links.Select(link => link.PackageId).Distinct(StringComparer.Ordinal).ToArray();
        var unlinkedParts = PartsOf(parts)
            .Where(part => links.All(link => NhAssistantPackagePartKinds.PartOf(link.PartKind, link.PartKey) != part))
            .ToArray();
        var installed = packageIds.Length == 1 ? await packages.FindPackageAsync(packageIds[0], cancellationToken) : null;
        if (installed is null || unlinkedParts.Length > 0)
        {
            return TaskResult<NhAssistantAgentPackageImportDto>.Failed(
                NhAssistantErrorCodes.PackageNotLinked,
                "Every part of the variant must be linked to one agent package.");
        }

        // An agent in code takes its instructions as text, as its update does.
        var inCode = liveRow.Source != NhAssistantAgentSources.Admin;
        if (inCode && parts.Definition?.InstructionsAsset is not null)
        {
            return TaskResult<NhAssistantAgentPackageImportDto>.Failed(
                NhAssistantValidationErrors.Failed(NhAssistantInstructionAssets.AssetField, NhAssistantFieldErrors.NotAllowed));
        }

        var package = new NhAssistantAgentPackage(
            NhAssistantAgentPackageFormat.Name,
            NhAssistantAgentPackageFormat.Version,
            new NhAssistantAgentPackageInfo(installed.Id, installed.Version),
            [LiveAgentOf(parts, liveRow, links)]);
        var options = await PromotionOptionsAsync(variant.Id, live.Id, cancellationToken);
        var planned = await PlanAsync(
            package,
            options,
            actorId,
            cancellationToken,
            new NhAssistantPackagePromotion(
                variant.Id,
                variantRow.Version,
                live.Id,
                reason,
                RelinkUncarried: uncarriedParts == NhAssistantUncarriedParts.Relink));
        if (planned.Failure is { } failure)
        {
            return TaskResult<NhAssistantAgentPackageImportDto>.Failed(failure);
        }

        var plan = planned.Plan!;
        if (!plan.Importable)
        {
            return TaskResult<NhAssistantAgentPackageImportDto>.Failed(PromotionRefusal(plan));
        }
        if (!await WriteAsync(plan, actorId, cancellationToken))
        {
            return TaskResult<NhAssistantAgentPackageImportDto>.Failed(
                NhAssistantAdminErrorCodes.VersionConflict,
                "The agents or the package changed in the meantime; nothing was promoted.");
        }

        await AuditWrittenAsync(plan, actorId, cancellationToken);
        await configurations.ReloadAsync(cancellationToken);
        return TaskResult<NhAssistantAgentPackageImportDto>.Succeeded(new NhAssistantAgentPackageImportDto(
            installed.Id,
            installed.Version,
            installed.Hash,
            plan.Parts.Select(part => part.ToDto()).ToArray()));
    }

    /// <summary>
    /// The variant's parts as the package of its live agent: the definition without what makes it a
    /// variant, with the live agent's display name and chat offer; the variant's locks per part. An
    /// agent in code takes the fields it can override and no run settings, as an overlay carries none.
    /// </summary>
    private static NhAssistantPackageAgent LiveAgentOf(
        NhAssistantPackageAgent variant,
        AssistantAgent live,
        IReadOnlyList<AssistantAgentPackageLink> links)
    {
        var inCode = live.Source != NhAssistantAgentSources.Admin;
        var definition = variant.Definition is not { } carried
            ? null
            : inCode
                ? new NhAssistantPackageAgentDefinition(
                    Description: carried.Description,
                    Instructions: carried.Instructions,
                    ToolSelectors: carried.ToolSelectors,
                    RequiredPolicy: carried.RequiredPolicy ?? string.Empty,
                    Autonomy: carried.Autonomy)
                : carried with
                {
                    DisplayName = live.DisplayName,
                    OfferedInChat = !live.HiddenFromChat,
                    StartsOff = null,
                    StartsInShadow = null,
                    ShadowOf = null
                };
        var agent = variant with
        {
            Id = live.Id,
            Source = inCode ? NhAssistantAgentSources.Code : NhAssistantAgentSources.Admin,
            Definition = definition,
            RunSettings = inCode ? null : variant.RunSettings,
            Groups = null,
            Hashes = null,
            Locks = null
        };

        var carriedParts = PartsOf(agent);
        var locks = links
            .Where(link => NhAssistantAgentPackageStore.LockedFieldsOf(link).Count > 0
                           && carriedParts.Contains(NhAssistantPackagePartKinds.PartOf(link.PartKind, link.PartKey), StringComparer.Ordinal))
            .ToDictionary(
                link => NhAssistantPackagePartKinds.PartOf(link.PartKind, link.PartKey),
                NhAssistantAgentPackageStore.LockedFieldsOf,
                StringComparer.Ordinal);
        return locks.Count == 0 ? agent : agent with { Locks = NormalizeLocks(locks) };
    }

    /// <summary>
    /// The import's options for a promotion: the live agent is relinked as a whole, so no part of it
    /// stays local, changed parts go live, and run settings that answer to a technical account keep
    /// the live agent's account, or take the variant's when the live agent has none.
    /// </summary>
    private async Task<NhAssistantAgentPackageImportOptions> PromotionOptionsAsync(
        string variantId,
        string liveAgentId,
        CancellationToken cancellationToken)
    {
        var liveSettings = await runConfiguration.FindRunSettingsAsync(liveAgentId, cancellationToken);
        var variantSettings = await runConfiguration.FindRunSettingsAsync(variantId, cancellationToken);
        var account = liveSettings?.ServiceAccountActorId ?? variantSettings?.ServiceAccountActorId;
        return new NhAssistantAgentPackageImportOptions(
            account is null ? null : new Dictionary<string, string>(StringComparer.Ordinal) { [liveAgentId] = account },
            NhAssistantSkillApplyModes.Live,
            [new NhAssistantPackagePartReference(liveAgentId)]);
    }

    /// <summary>
    /// The failure of a promotion whose plan refused a part: the import's
    /// <c>assistant-package-refused</c> with <c>parts: invalid</c>, and per refused part its field
    /// errors as <c>parts.{part}.{field}</c>, or <c>parts.{part}: invalid</c> for a part refused with a
    /// code, since a promotion has no preview that reports them.
    /// </summary>
    private static TaskResult PromotionRefusal(NhAssistantPackagePlan plan)
    {
        var errors = new NhAssistantValidationErrors().Add("parts", NhAssistantFieldErrors.Invalid);
        foreach (var part in plan.Parts.Where(item => item.Refused))
        {
            if (part.Errors.Count == 0)
            {
                errors.Add("parts." + part.Part, NhAssistantFieldErrors.Invalid);
                continue;
            }

            foreach (var (field, codes) in part.Errors)
            {
                foreach (var code in codes)
                {
                    errors.Add("parts." + part.Part + "." + field, code);
                }
            }
        }

        return errors.ToResult(NhAssistantErrorCodes.PackageRefused, "The import of the variant's package version refused a part; nothing was promoted.");
    }
}
