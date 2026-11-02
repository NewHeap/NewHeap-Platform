using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AI.Chat.Entities;

namespace NewHeap.Platform.AI.Chat.Persistence;

/// <summary>
/// An administrator's settings per alert kind (FB87 phase 2): one row per kind with its version, which a
/// change names, so two administrators never overwrite each other unseen. Every read goes to the
/// database, so a change applies on every host to the next alert that is raised or delivered.
/// </summary>
internal sealed class NhAssistantAlertKindSettingStore(NhAssistantDbContextFactory contextFactory)
{
    /// <summary>
    /// The settings of the kinds <paramref name="kindIds"/> names, by kind; a kind without settings is
    /// left out.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, AssistantAlertKindSetting>> GetAsync(
        IReadOnlyCollection<string> kindIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(kindIds);
        if (kindIds.Count == 0)
        {
            return new Dictionary<string, AssistantAlertKindSetting>(StringComparer.Ordinal);
        }

        var ids = kindIds.Distinct(StringComparer.Ordinal).ToList();
        await using var context = contextFactory.CreateDbContext();
        var settings = await context.AlertKindSettings
            .AsNoTracking()
            .Where(setting => ids.Contains(setting.KindId))
            .ToListAsync(cancellationToken);
        return settings.ToDictionary(setting => setting.KindId, StringComparer.Ordinal);
    }

    /// <summary>
    /// Saves the settings of <see cref="AssistantAlertKindSetting.KindId"/> when its stored version is
    /// <paramref name="expectedVersion"/>: 0 before the first settings. A stale version, or another change
    /// that saved first, is <see cref="NhAssistantStoreOutcome.Conflict"/> and saves nothing. Returns the
    /// settings as they are stored now and what they were before.
    /// </summary>
    public async Task<NhAssistantStoreResult<NhAssistantAlertKindSettingChange>> SaveAsync(
        AssistantAlertKindSetting settings,
        int expectedVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await using var context = contextFactory.CreateDbContext();
        var existing = await context.AlertKindSettings.SingleOrDefaultAsync(item => item.KindId == settings.KindId, cancellationToken);
        if ((existing?.Version ?? 0) != expectedVersion)
        {
            return NhAssistantStoreResult<NhAssistantAlertKindSettingChange>.Failed(NhAssistantStoreOutcome.Conflict);
        }

        var before = existing is null ? null : Copy(existing);
        if (existing is null)
        {
            settings.Version = 1;
            settings.CreatedAt = settings.UpdatedAt;
            settings.CreatedBy = settings.UpdatedBy;
            settings.ConcurrencyStamp = Guid.NewGuid();
            context.AlertKindSettings.Add(settings);
            existing = settings;
        }
        else
        {
            existing.Enabled = settings.Enabled;
            existing.DeliveryMode = settings.DeliveryMode;
            existing.DigestTimes = settings.DigestTimes;
            existing.WorkdaysOnly = settings.WorkdaysOnly;
            existing.ImmediatelyFrom = settings.ImmediatelyFrom;
            existing.QuietHoursStart = settings.QuietHoursStart;
            existing.QuietHoursEnd = settings.QuietHoursEnd;
            existing.Version++;
            existing.UpdatedAt = settings.UpdatedAt;
            existing.UpdatedBy = settings.UpdatedBy;
            existing.Reason = settings.Reason;
            existing.ConcurrencyStamp = Guid.NewGuid();
        }

        if (!await NhAssistantRunConfigurationStore.TrySaveAsync(context, cancellationToken))
        {
            return NhAssistantStoreResult<NhAssistantAlertKindSettingChange>.Failed(NhAssistantStoreOutcome.Conflict);
        }

        return NhAssistantStoreResult<NhAssistantAlertKindSettingChange>.Ok(new NhAssistantAlertKindSettingChange(Copy(existing), before));
    }

    private static AssistantAlertKindSetting Copy(AssistantAlertKindSetting setting)
    {
        return new AssistantAlertKindSetting
        {
            KindId = setting.KindId,
            Enabled = setting.Enabled,
            DeliveryMode = setting.DeliveryMode,
            DigestTimes = setting.DigestTimes,
            WorkdaysOnly = setting.WorkdaysOnly,
            ImmediatelyFrom = setting.ImmediatelyFrom,
            QuietHoursStart = setting.QuietHoursStart,
            QuietHoursEnd = setting.QuietHoursEnd,
            Version = setting.Version,
            CreatedAt = setting.CreatedAt,
            CreatedBy = setting.CreatedBy,
            UpdatedAt = setting.UpdatedAt,
            UpdatedBy = setting.UpdatedBy,
            Reason = setting.Reason,
            ConcurrencyStamp = setting.ConcurrencyStamp
        };
    }
}

/// <summary>
/// A saved change of a kind's settings: what is stored now and what was stored before, or
/// <see langword="null"/> for the kind's first settings.
/// </summary>
internal sealed record NhAssistantAlertKindSettingChange(AssistantAlertKindSetting Saved, AssistantAlertKindSetting? Before);
